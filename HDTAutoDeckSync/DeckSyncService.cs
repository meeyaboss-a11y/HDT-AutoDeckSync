using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows; // System.Windows.Application を使うために追加
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace AutoDeckSync
{
    public class DeckSyncService : IDisposable
    {
        private static readonly TimeSpan ModeWatchInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromMinutes(2);
        private const string CollectionModeName = "COLLECTIONMANAGER";

        private readonly object _syncLock = new object();
        private readonly string _logFilePath;

        private Timer _modeWatchTimer;
        private Timer _fallbackPollTimer;
        private string _lastMode;
        private bool _started;
        private bool _disposed;
        private CancellationTokenSource _syncCts;

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

            GameEvents.OnGameEnd.Add(OnGameEnd);
            _modeWatchTimer = new Timer(_ => WatchMode(), null, ModeWatchInterval, ModeWatchInterval);
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
            _syncCts?.Cancel();
            _syncCts = new CancellationTokenSource();
            var token = _syncCts.Token;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(2000);
                    TrySyncWithRetries(5, token);
                }
                catch (Exception ex)
                {
                    Log("ScheduleSync error: " + ex.Message);
                }
            });
        }

        private void TrySyncWithRetries(int maxRetries, CancellationToken token)
        {
            if (_disposed) return;

            for (int i = 0; i < maxRetries; i++)
            {
                if (token.IsCancellationRequested || _disposed)
                {
                    Log("Sync task cancelled due to newer event.");
                    return;
                }

                bool success = false;
                if (Monitor.TryEnter(_syncLock))
                {
                    try
                    {
                        success = Sync();
                    }
                    catch (Exception ex)
                    {
                        Log("Sync failed with an unexpected error: " + ex);
                        success = true;
                    }
                    finally
                    {
                        Monitor.Exit(_syncLock);
                    }
                }

                if (success || _disposed || token.IsCancellationRequested) break;

                if (i < maxRetries - 1)
                {
                    Log($"Decks not fully loaded yet. Retrying in 2 seconds... (Attempt {i + 1}/{maxRetries})");
                    Thread.Sleep(2000);
                }
            }
        }

        private bool Sync()
        {
            var game = Hearthstone_Deck_Tracker.API.Core.Game;

            // 1. 【安全判定】メニュー内にいるか、または直前までコレクション画面にいた状態
            bool isInMenuOrTransition = (game != null && game.IsInMenu) || !string.IsNullOrEmpty(_lastMode);
            if (game == null || !isInMenuOrTransition)
            {
                return true;
            }

            // 【プロセスチェック】ゲーム自体が生きているか物理チェック
            var processes = System.Diagnostics.Process.GetProcessesByName("Hearthstone");
            if (processes.Length == 0)
            {
                Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("Hearthstone process not found. Skipping sync for safety.");
                return true;
            }

            List<long> liveDeckIds = new List<long>();
            int rawMirrorCount = 0;

            try
            {
                var mirrorDecks = HearthMirror.Reflection.Client.GetDecks();

                if (mirrorDecks == null)
                {
                    // 💡 return true に修正：削除を行わず、HDTのシステムをロックせずに安全にスルーします
                    Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("HearthMirror returned null. Process might be lagging. Skipping this tick.");
                    return true;
                }
                else
                {
                    // メモリ上のデッキが0件の場合、一時的な読み込みエラーの可能性が非常に高いです。
                    // 💡 ここも return true に修正：全消しに進まず、安全に今回の同期をスルーします
                    if (mirrorDecks.Count == 0)
                    {
                        Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("HearthMirror reported 0 decks. Skipping to prevent total wipe.");
                        return true;
                    }

                    Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"HearthMirror reported {mirrorDecks.Count} raw decks in memory.");
                    rawMirrorCount = mirrorDecks.Count;

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
                Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("Could not read decks from Reflection Client: " + ex.Message);
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
                Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"Sync complete. Both lists match. (Active: {liveDeckIds.Count} decks)");
                return true;
            }

            // ★★★【進化した全消し拒否ロック】★★★
            if (removed.Count >= candidates.Count && rawMirrorCount > 0)
            {
                Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[CRITICAL WARNING] Blocked mass-deletion! Attempted to remove all {removed.Count} decks, but HearthMirror still sees {rawMirrorCount} decks in game. This is a reading discrepancy.");
                return true;
            }

            // UIスレッドでの削除処理
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    foreach (var deck in removed)
                    {
                        DeckList.Instance.Decks.Remove(deck);
                    }
                    DeckList.Save();
                }
                catch (Exception ex)
                {
                    Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("Error removing decks on UI thread: " + ex);
                }
            });

            var names = string.Join(", ", removed.Select(d => $"{d.Name} [{d.Class}]"));
            Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[SUCCESS] Actually removed {removed.Count} deck(s) from HDT: {names}");

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

            _syncCts?.Cancel();
            _syncCts?.Dispose();

            _modeWatchTimer?.Dispose();
            _modeWatchTimer = null;

            _fallbackPollTimer?.Dispose();
            _fallbackPollTimer = null;

            Log("AutoDeckSync stopped.");
        }
    }
}