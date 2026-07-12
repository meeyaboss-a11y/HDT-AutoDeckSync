using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows; // UIスレッド（Dispatcher）操作のために必要
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace AutoDeckSync
{
    /// <summary>
    /// HDT上に残っている「実際にはもうHearthstone本体に存在しないデッキ」を検出し、
    /// 自動的にHDTのデッキリストから取り除くプラグインサービス。
    /// </summary>
    public class DeckSyncService : IDisposable
    {
        private static readonly TimeSpan ModeWatchInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromMinutes(2);
        private const string CollectionModeName = "COLLECTIONMANAGER";

        private readonly object _syncLock = new object();
        private readonly SemaphoreSlim _syncSemaphore = new SemaphoreSlim(1, 1);
        private readonly string _logFilePath;

        private Timer _modeWatchTimer;
        private Timer _fallbackPollTimer;
        private string _lastMode;
        private bool _started;
        private bool _disposed;

        public DeckSyncService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logDir = Path.Combine(appData, "HearthstoneDeckTracker", "Plugins", "AutoDeckSync");
            _logFilePath = Path.Combine(logDir, "sync.log");
        }

        public void Start()
        {
            if (_started) return;
            _started = true;

            // 対戦終了イベントの購読
            GameEvents.OnGameEnd.Add(OnGameEnd);

            // 画面モードの変更を監視する軽量タイマー
            _modeWatchTimer = new Timer(_ => WatchMode(), null, ModeWatchInterval, ModeWatchInterval);

            // 保険としての低頻度チェック
            _fallbackPollTimer = new Timer(_ => RunNow(), null, FallbackPollInterval, FallbackPollInterval);

            Log("AutoDeckSync started.");
        }

        public void RunNow()
        {
            ScheduleSync();
        }

        private void OnGameEnd()
        {
            if (_disposed) return;
            Log("Game ended, scheduling sync.");
            ScheduleSync();
        }

        private void WatchMode()
        {
            if (_disposed) return;

            string currentMode;
            try
            {
                var game = Hearthstone_Deck_Tracker.API.Core.Game;
                currentMode = game?.CurrentMode.ToString();
            }
            catch
            {
                return;
            }

            var previousMode = _lastMode;
            _lastMode = currentMode;

            if (previousMode != currentMode)
            {
                if (currentMode == CollectionModeName)
                {
                    Log("Detected entering the Collection screen, scheduling a sync.");
                    ScheduleSync();
                }
                else if (previousMode == CollectionModeName)
                {
                    Log("Detected leaving the Collection screen, scheduling a sync.");
                    ScheduleSync();
                }
            }
        }

        private void ScheduleSync()
        {
            // CancellationTokenによる途中キャンセルを廃止し、セマフォでタスクを1列に並べて順番待ちさせます。
            // これにより、画面開閉が高速で連打されても、前の削除処理が途中でぶった切られるのを防ぎます。
            ThreadPool.QueueUserWorkItem(async _ =>
            {
                if (_disposed) return;

                await _syncSemaphore.WaitAsync();
                try
                {
                    // 画面遷移アニメーションやロードのラグを安全に吸収するため、少し長めに待機
                    Thread.Sleep(2500);
                    TrySyncWithRetries(3);
                }
                catch (Exception ex)
                {
                    Log("ScheduleSync error: " + ex.Message);
                }
                finally
                {
                    _syncSemaphore.Release();
                }
            });
        }

        private void TrySyncWithRetries(int maxRetries)
        {
            if (_disposed) return;

            for (int i = 0; i < maxRetries; i++)
            {
                if (_disposed) return;

                bool success = false;
                lock (_syncLock)
                {
                    try
                    {
                        success = Sync();
                    }
                    catch (Exception ex)
                    {
                        Log("Sync failed with an unexpected error: " + ex);
                        success = true; // 予期せぬ例外時は安全のためリトライを停止
                    }
                }

                if (success || _disposed) break;

                Log($"Memory not ready. Retrying in 2 seconds... (Attempt {i + 1}/{maxRetries})");
                Thread.Sleep(2000);
            }
        }

        private bool Sync()
        {
            var game = Hearthstone_Deck_Tracker.API.Core.Game;

            // 【安全判定】メニュー内にいるか、または直前までコレクション画面にいた（_lastModeが記録されている）状態
            // どちらにも当てはまらない完全な「対戦中」などの時だけ処理をスキップします。
            bool isInMenuOrTransition = (game != null && game.IsInMenu) || !string.IsNullOrEmpty(_lastMode);

            if (game == null || !isInMenuOrTransition)
            {
                return true;
            }

            List<long> liveDeckIds = new List<long>();
            try
            {
                // メモリ上の生データを直接参照
                var mirrorDecks = HearthMirror.Reflection.Client.GetDecks();

                if (mirrorDecks == null)
                {
                    // コレクション画面のログ（_lastMode）が残っている状態での null は「デッキが本当に0件」の挙動です。
                    // ロード未完了と誤認させず、空のリストとしてそのまま下へ流します。
                    if (!string.IsNullOrEmpty(_lastMode))
                    {
                        Log("HearthMirror returned null (0 decks verified in memory).");
                    }
                    else
                    {
                        Log("HearthMirror returned null (Process not ready).");
                        return false; // 本当にゲームプロセスが読めない状態のときだけリトライ
                    }
                }
                else
                {
                    Log($"HearthMirror reported {mirrorDecks.Count} raw decks in memory.");

                    liveDeckIds = mirrorDecks
                        .Where(d => d != null)
                        .Select(d => d.Id)
                        .Where(id => id != 0)
                        .Distinct()
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log("Could not read decks from Reflection Client: " + ex.Message);
                return true;
            }

            var liveIds = new HashSet<long>(liveDeckIds);
            var candidates = DeckList.Instance.Decks
                .Where(d => d.HsId != 0 && !d.Archived)
                .ToList();

            var removed = new List<Deck>();
            foreach (var deck in candidates)
            {
                if (!liveIds.Contains(deck.HsId))
                    removed.Add(deck);
            }

            if (removed.Count == 0)
            {
                Log($"Sync complete. Both lists match. (Active: {liveDeckIds.Count} decks)");
                return true;
            }

            // UIスレッドを絶対に途中で殺させないためのInvoke
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    foreach (var deck in removed)
                    {
                        DeckList.Instance.Decks.Remove(deck);
                    }

                    // ObservableCollection の挙動により、Remove された時点で
                    // HDTの画面からは自動的に消えるようになっています。
                    DeckList.Save();
                }
                catch (Exception ex)
                {
                    Log("Error removing decks on UI thread: " + ex);
                }
            });

            var names = string.Join(", ", removed.Select(d => $"{d.Name} [{d.Class}]"));
            Log($"[SUCCESS] Actually removed {removed.Count} deck(s) from HDT: {names}");

            return true;
        }

        private void Log(string message)
        {
            try
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(
                    _logFilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _modeWatchTimer?.Dispose();
            _modeWatchTimer = null;

            _fallbackPollTimer?.Dispose();
            _fallbackPollTimer = null;

            _syncSemaphore?.Dispose();

            Log("AutoDeckSync stopped.");
        }
    }
}