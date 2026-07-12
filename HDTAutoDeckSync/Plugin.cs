using System;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.Plugins;

namespace AutoDeckSync
{
    public class Plugin : IPlugin
    {
        private DeckSyncService _syncService;

        public string Name => "AutoDeckSync";

        public string Description =>
            "Automatically removes Hearthstone decks that no longer exist in HDT.";

        public string ButtonText => "Sync";

        public string Author => "Tooru";

        public Version Version => new Version(1, 0, 0);

        public MenuItem MenuItem => null;

        public void OnLoad()
        {
            _syncService = new DeckSyncService();
            _syncService.Start();
        }

        public void OnUnload()
        {
            _syncService?.Dispose();
        }

        public void OnButtonPress()
        {
            _syncService?.RunNow();
        }

        public void OnUpdate()
        {
            // HDTから約100msごとに呼ばれるが、このプラグインは
            // イベント駆動 + 定期ポーリング(DeckSyncService内)で同期するため、ここでは何もしない。
        }
    }
}