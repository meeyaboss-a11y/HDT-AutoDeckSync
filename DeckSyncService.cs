using System;
using System.Collections.Generic;
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
        private const string CollectionModeName = "COLLECTIONMANAGER";

        private readonly object _syncLock = new object();
        private string _lastMode;
        private bool _started;
        private bool _disposed;
        private CancellationTokenSource _syncCts;

        public DeckSyncService()
        {
        }

        public void Start()
        {
            if (_started) return;
            _started = true;

            // 対戦終了時の自動同期イベント
            GameEvents.OnGameEnd.Add(OnGameEnd);

            // 💡 引数の型ミスマッチを避けるため、ラムダ式 `_ => OnModeChanged()` でイベントを受け取ります
            GameEvents.OnModeChanged.Add(_ => OnModeChanged());

            Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("[AutoDeckSync] Started (Event-driven mode).");
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

        private void OnModeChanged()
        {
            if (_disposed) return;

            var game = Hearthstone_Deck_Tracker.API.Core.Game;
            if (game == null) return;

            // 🛑 エラーの原因だった `IsGameplay` を削除しました。
            // 代わりに、下の `IsInMenu` 判定を厳格に使うことで対戦中を安全に弾きます。

            string currentMode;
            try
            {
                currentMode = game.CurrentMode.ToString();
            }
            catch
            {
                return;
            }

            var previousMode = _lastMode;
            _lastMode = currentMode;

            // コレクション画面に入った瞬間、または出た瞬間だけ同期を起動
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
            if (game == null) return true;

            // 🛑【厳重ガード】メニュー画面内（IsInMenuがtrue）ではない＝対戦中や観戦中などは即終了します
            if (!game.IsInMenu)
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

            // 【全消し拒否ロック】
            if (removed.Count >= candidates.Count && rawMirrorCount > 0)
            {
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

            Hearthstone_Deck_Tracker.Utility.Logging.Log.Info("[AutoDeckSync] Stopped.");
        }
    }
}