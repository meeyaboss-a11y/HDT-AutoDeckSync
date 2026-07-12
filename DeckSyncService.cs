using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
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

        private Timer _modeWatchTimer;
        private Timer _fallbackPollTimer;
        private string _lastMode;
        private bool _started;
        private bool _disposed;
        private CancellationTokenSource _syncCts;

        public DeckSyncService()
        {
            // 不要になった sync.log のパス定義やディレクトリ作成処理を完全に削除しました
        }

        public void Start()
        {
            if (_started) return;
            _started = true;

            GameEvents.OnGameEnd.Add(OnGameEnd);
            _modeWatchTimer = new Timer(_ => WatchMode(), null, ModeWatchInterval, ModeWatchInterval);
            _fallbackPollTimer = new Timer(_ => RunNow(), null, FallbackPollInterval, FallbackPollInterval);

            // 起動ログをHDT公式へ統合（不要ならこの行ごと削除しても構いません）
            Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("[AutoDeckSync] Started.");
        }

        public void RunNow()
        {
            ScheduleSync();
        }

        private void OnGameEnd()
        {
            if (_disposed) return;
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
                if (currentMode == CollectionModeName || previousMode == CollectionModeName)
                {
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
                    Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("[AutoDeckSync Error] ScheduleSync error: " + ex.Message);
                }
            });
        }

        private void TrySyncWithRetries(int maxRetries, CancellationToken token)
        {
            if (_disposed) return;

            for (int i = 0; i < maxRetries; i++)
            {
                if (token.IsCancellationRequested || _disposed) return;

                bool success = false;
                if (Monitor.TryEnter(_syncLock))
                {
                    try
                    {
                        success = Sync();
                    }
                    catch (Exception ex)
                    {
                        Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("[AutoDeckSync Error] Sync failed unexpected: " + ex);
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
                    Thread.Sleep(2000);
                }
            }
        }

        private bool Sync()
        {
            var game = Hearthstone_Deck_Tracker.API.Core.Game;

            bool isInMenuOrTransition = (game != null && game.IsInMenu) || !string.IsNullOrEmpty(_lastMode);
            if (game == null || !isInMenuOrTransition)
            {
                return true;
            }

            var processes = System.Diagnostics.Process.GetProcessesByName("Hearthstone");
            if (processes.Length == 0)
            {
                return true;
            }

            List<long> liveDeckIds = new List<long>();
            int rawMirrorCount = 0;

            try
            {
                var mirrorDecks = HearthMirror.Reflection.Client.GetDecks();

                if (mirrorDecks == null || mirrorDecks.Count == 0)
                {
                    return true;
                }

                rawMirrorCount = mirrorDecks.Count;

                liveDeckIds = mirrorDecks
                    .Where(d => d != null)
                    .Select(d => d.Id)
                    .Where(id => id != 0)
                    .Distinct()
                    .ToList();
            }
            catch (Exception ex)
            {
                // ⚠️ 例外エラーが発生した場合のみHDT公式ログへ記録
                Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("[AutoDeckSync Error] Could not read decks from Reflection Client: " + ex.Message);
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
                return true;
            }

            // ★★★【進化した全消し拒否ロック】★★★
            if (removed.Count >= candidates.Count && rawMirrorCount > 0)
            {
                // ⚠️ メモリ読み込み異常と思われる全消去をブロックした「重大な警告」のみ記録
                Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[AutoDeckSync CRITICAL] Blocked mass-deletion! Attempted to remove all {removed.Count} decks, but HearthMirror still sees {rawMirrorCount} decks.");
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
                    Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("[AutoDeckSync Error] UI thread remove failed: " + ex);
                }
            });

            var names = string.Join(", ", removed.Select(d => $"{d.Name} [{d.Class}]"));
            Hearthstone_Deck_Tracker.Utility.Logging.Log.Info($"[AutoDeckSync SUCCESS] Removed {removed.Count} deck(s): {names}");

            return true;
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

            Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("[AutoDeckSync] Stopped.");
        }
    }
}