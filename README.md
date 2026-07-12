# HDT-AutoDeckSync

ハースストーンのゲーム内で削除したデッキを、Hearthstone Deck Tracker (HDT) のリストからも自動的に取り除くプラグインです。
This HDT plugin automatically detects and removes decks from your Hearthstone Deck Tracker that you have already deleted within the Hearthstone game client.

## ✨ 特徴 / Features
コレクション画面を閉じた時に、メモリ上のデッキとHDTのリストを自動同期します。  
万が一の同期失敗に備え、ゲーム内のデッキが0件として検出された場合は同期処理を行いません。  
余計な外部DLLに依存せず、HDTの標準APIのみで動作するため軽量ですが、環境によっては動作が不安定になり、HDTが正常に機能しなくなるリスクがあるかもしれません。
導入は自己責任でお願いいたします。

Automatically synchronizes your HDT deck list when leaving the Collection manager.  
To safeguard against synchronization failures, the process will be skipped entirely if no decks are detected in game.  
Completely lightweight and depending only on native HDT APIs, however, there may be a risk of instability causing HDT to malfunction depending on your environment. Please use this plugin at your own risk.

## 💾 インストール方法 / Installation
1. 右側の **Releases** から `AutoDeckSync.dll` をダウンロードします。
   Download `AutoDeckSync.dll` from the **Releases** section.
2. HDTのメニューから `オプション` ＞ `プラグイン` ＞ `プラグインフォルダーを開く` をクリックします。
   In HDT, go to `Options` > `Plugins` > `Open Plugins Folder`.
3. 開いたフォルダーの中に `AutoDeckSync` という名前の新しいフォルダーを作り、その中にダウンロードした `AutoDeckSync.dll` を入れます。
   Create a new folder named `AutoDeckSync` inside the plugins directory, and move the downloaded `.dll` file into it.
4. HDTを再起動し、プラグイン設定画面で本プラグインを「有効」にしてください。
   Restart HDT and enable the plugin in the options menu.
