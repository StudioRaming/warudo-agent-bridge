# WARUDO Agent Bridge

[English](README.md) | [한국어](README.ko.md) | **日本語**

**AIに話しかけると、AIがWARUDOを操作してくれます。**

「笑った顔で手を振るポーズを作って、正面から写真を撮ってよ。」このようにチャットで送ると、AIはWARUDOでカメラを移動し、表情を変え、ポーズを作り、写真を撮って見せてくれます。コーディングを知る必要はありません。インストール時に1行のコマンドを貼り付けるだけです。

<p>
<img src="docs/images/photo-full.jpg" width="24%" alt="正面全身の写真">
<img src="docs/images/face-wink.jpg" width="24%" alt="ウインク表情">
<img src="docs/images/pose-wave.jpg" width="24%" alt="手を振るポーズ">
<img src="docs/images/pose-kneel.jpg" width="24%" alt="膝をついたポーズ">
</p>

<sub>上の写真は全てWARUDOのデフォルトキャラクター(Shipilka)で、下の[例](#できること)のリクエストをこのブリッジで実際に実行して撮った画面です。</sub>

> WARUDO・HakuyaLabsとは無関係の非公式ツールです。無料（MITライセンス）、StudioRaming製。

## 目次

1. [MCPとは？（初めての人はここから）](#mcpとは)
2. [できること（例の集まり）](#できること)
3. [必要なもの](#必要なもの)
4. [インストールする（手順通りに）](#インストールする)
5. [AIアプリに接続する](#aiアプリに接続する)：Claude デスクトップアプリ、Claude Code、Codex、ChatGPT デスクトップアプリ
6. [最初に試す](#最初に試す)
7. [安全に使う](#安全に使う)
8. [よくある質問](#よくある質問)
9. [問題が起きたとき](#問題が起きたとき)
10. [詳細情報（上級者向け）](#詳細情報上級者向け)

## MCPとは？

ChatGPTやClaudeのようなAIは、もともと**文字で会話することしか**できません。自分のパソコンで起動しているプログラムのボタンを直接押すことはできません。

**MCP**（Model Context Protocol）は、AIに**ツールを与える共通規格**です。USBを思い浮かべるとわかりやすいですね。規格が同じなので、どのAIアプリにも差し込んで使えます。

**WARUDO Agent Bridge**はWARUDO用のMCPツール集です。インストールすると、AIが「カメラを移動する」「表情を変える」「ポーズを作る」「写真を撮る」といったツール66個を使えるようになります。

```
私 (チャットでお願い)
 ↓
AIアプリ (Claude、Codex、ChatGPT デスクトップ)
 ↓ MCP
WARUDO Agent Bridge (自分のPC内で動作)
 ↓
WARUDO
```

知っておくといいこと：

- **AIアプリとWARUDOが同じPCで起動していなければなりません。** ブリッジは自分のPC内のファイルでWARUDOとやり取りするだけで、インターネットポートを開きません。
- **会話内容はAIサービスに送られます。** 自分が送ったメッセージとAIが読んだ結果（シーン情報、撮った写真を含む）は、通常のチャットと同じようにそのAI企業のサーバーで処理されます。
- **ブリッジは無料です。** AIアプリの利用料は各サービスの料金プランに従います。
- **Windows専用です。** WARUDOがWindowsプログラムだからです。

## できること

AIに送ったメッセージとその結果です。写真は各リクエストに合わせたツールをWARUDOのデフォルトキャラクター(Shipilka)でこのブリッジで実際に実行して撮ったもので、AIの返答は短くまとめて転記しました。「使ったツール」はAIが自動で選ぶものなので、覚える必要はありません。

### 📸 写真とカメラ

| 💬 AIに送ったメッセージ | 🤖 結果 |
| --- | --- |
| **「キャラクターの全身を正面から撮ってよ。」** | <img src="docs/images/photo-full.jpg" width="260" alt="正面全身"><br>「カメラをキャラクターの正面に移動して撮りました。」<br><sub>使ったツール: `warudo_camera_pose` → `warudo_camera_capture`</sub> |
| **「顔だけを大きくクローズアップして撮ってよ。」** | <img src="docs/images/photo-face.jpg" width="260" alt="顔のクローズアップ"><br>「頭の骨の位置を読んで、カメラを顔の前に付けました。」<br><sub>使ったツール: `warudo_character_pose_get` → `warudo_camera_pose` → `warudo_camera_capture`</sub> |
| **「下から見上げるかっこいい構図で撮ってよ。」** | <img src="docs/images/photo-low.jpg" width="260" alt="ロウアングル"><br>「膝の高さから斜めに見上げるように撮りました。」<br><sub>使ったツール: `warudo_camera_pose` → `warudo_camera_capture`</sub> |

**「前、斜め前、横、後ろから1枚ずつ撮って、見せてよ。」**

<img src="docs/images/camera-turnaround.jpg" width="720" alt="4方向から撮った写真">

「画角を60度に広げて、キャラクターの周りを回りながら4枚撮りました。」<br>
<sub>使ったツール: `warudo_camera_zoom` → `warudo_camera_pose` → `warudo_camera_capture` (4回繰り返す、ここでは4枚を1枚に貼り付けています)</sub>

### 😊 表情

キャラクターごとに表情の名前が異なるため、AIは先にキャラクターの表情リスト（ブレンドシェイプ）を読んで、リクエストに合ったものを有効にします。このキャラクターには106個ありました。

| 💬 AIに送ったメッセージ | 🤖 結果 |
| --- | --- |
| **「満面の笑みにしてよ。」** | <img src="docs/images/face-smile.jpg" width="260" alt="笑う表情"><br>「笑う目(笑い)、笑う眉(にこり)、口角(口角上げ)、開いた口(あ)、頬の赤み(照れ)をオンにしました。」<br><sub>使ったツール: `warudo_character_blendshapes` → `warudo_character_blendshape`</sub> |
| **「ウインクして、舌を少し出してよ。」** | <img src="docs/images/face-wink.jpg" width="260" alt="ウインク表情"><br>「ウインク(ウィンク)と舌を出す(てへぺろ)をオンにしました。」<br><sub>使ったツール: `warudo_character_blendshape`</sub> |
| **「びっくりした表情！」** | <img src="docs/images/face-surprised.jpg" width="260" alt="びっくり表情"><br>「びっくりした目(びっくり)、びっくりした口(口_驚き)、上げた眉(上)をオンにしました。」<br><sub>使ったツール: `warudo_character_blendshape`</sub> |
| **「表情を元に戻してよ。」** | 「私が上書きした表情の値を全部消しました。元の表情に戻りました。」<br><sub>使ったツール: `warudo_character_blendshape` (`clear`)</sub> |

### 🙋 ポーズ

ポーズは「腕をこの方向に伸ばす」というような方向で作ります。人間ができない角度に関節が曲がらないようにブリッジが防ぎ、ポーズが画面に実際に見えた後に結果を返します。

| 💬 AIに送ったメッセージ | 🤖 結果 |
| --- | --- |
| **「手を振るポーズを作ってよ。」** | <img src="docs/images/pose-wave.jpg" width="260" alt="手を振る"><br>「右腕を横に上げて、肘を立てました。頭も少し傾けました。」<br><sub>使ったツール: `warudo_character_pose_set`</sub> |
| **「万歳してよ！」** | <img src="docs/images/pose-banzai.jpg" width="260" alt="万歳"><br>「両腕を上にまっすぐ伸ばして、頭も少し上げました。」<br><sub>使ったツール: `warudo_character_pose_set`</sub> |
| **「両手を腰に当てて、堂々と立ってよ。」** | <img src="docs/images/pose-hips.jpg" width="260" alt="腰に手"><br>「肘を外側に引き出して、手を腰に当てました。胸を張って、あごを少し上げました。」<br><sub>使ったツール: `warudo_character_pose_set`</sub> |
| **「膝をついて座って、両手を合わせてよ。」** | <img src="docs/images/pose-kneel.jpg" width="260" alt="膝をつく"><br>「骨盤を床から33.5cm高さまで下げて、脛を床に寝かせました。両手は前で合わせました。」<br><sub>使ったツール: `warudo_character_pose_set` (`hipsHeight`)</sub> |

**「ポーズがちゃんとできてるか確認してよ。足が床に埋もれてないか？」**

<img src="docs/images/analysis-kneel.jpg" width="640" alt="分析レンダー：正面と右から見た骨組み">

「衣装と背景を除いて、骨組みを前と横から描いてみました。膝と足は床に触れていて、靴の先が0.5cm程度埋まっています。しっぽの飾りは床の18cm下まで届いているので、座った姿勢では床に埋もれて見えるでしょう。」<br>
<sub>使ったツール: `warudo_analysis_capture` (WARUDO画面は変わりません)</sub>

| 💬 AIに送ったメッセージ | 🤖 結果 |
| --- | --- |
| **「このポーズをUnityで使う.animファイルで保存してよ。」** | 「`Kneel_Readme.anim`(70KB)で保存しました。Unityプロジェクトに入れるとヒューマノイドアニメーションとして使えます。」<br><sub>使ったツール: `warudo_character_pose_save`</sub> |
| **「ポーズを解いて、元の待機モーションに戻してよ。」** | 「ポーズを止めて、元の待機アニメーション(AGIA_Idle_generic_01)に戻しました。」<br><sub>使ったツール: `warudo_character_pose_clear`</sub> |

### 💃 アニメーションと衣装

| 💬 AIに送ったメッセージ | 🤖 結果 |
| --- | --- |
| **「ノリノリで踊ってよ。」** | <img src="docs/images/anim-dance.jpg" width="260" alt="踊っている様子"><br>「WARUDOのデフォルトダンスの中の『ハイリズムダンス 01』を待機アニメーションとして流しました。」<br><sub>使ったツール: `warudo_resource_list` → `warudo_character_animation`</sub> |
| **「帽子と翼を外してよ。」** | <img src="docs/images/outfit-off.jpg" width="260" alt="帽子と翼を外した様子"><br>「キャラクター内から帽子(HeadItem_Hat、HeadItem_Halo)と翼(Item_Wing、Item_SmallWing)オブジェクトを見つけて、オフにしました。」<br><sub>使ったツール: `warudo_hierarchy_list` → `warudo_object_active`</sub> |

### 🔎 シーンを確認して戻す

| 💬 AIに送ったメッセージ | 🤖 結果 |
| --- | --- |
| **「WARUDOと接続できてる？」** | 「接続できてます。ブリッジ 0.2.0がWARUDO内で動いていて、今開いているシーン名も確認しました。」<br><sub>使ったツール: `warudo_status`</sub> |
| **「今のシーンに何があるの？」** | 「カメラ1、環境、ディレクショナルライト、キャラクター1(Shipilka)、FPSカウンター、初心者設定ヘルパーがあります。」<br><sub>使ったツール: `warudo_scene_get`</sub> |
| **「ブループリントは何があるの？」** | 「『新しいブループリント』が2つ有効になっていますが、どちらもノードがない空のブループリントです。」<br><sub>使ったツール: `warudo_blueprint_list`</sub> |
| **「色々試すから、今の状態から保存しておいてよ。」** | 「復帰用のスナップショットを作りました。シーンファイルには触れていません。戻したいと言ったら戻します。」<br><sub>使ったツール: `warudo_scene_snapshot` (戻すときは `warudo_scene_restore`)</sub> |

### 💡 こんなリクエストもできます

| リクエストの例 | AIが使うツール |
| --- | --- |
| 「キャラクターを右に1メートル移動して、カメラの方を向いてよ。」 | `warudo_asset_transform` |
| 「照明を夕焼けのようなオレンジ色に変えて、ちょっと明るくしてよ。」 | `warudo_asset_fields` → `warudo_asset_patch` |
| 「背景を別の環境に変えてよ。何があるか先に見せて。」 | `warudo_resource_list` → `warudo_asset_source` → `warudo_asset_wait_loaded` |
| 「手を振る動きを1回だけ再生してよ。」 | `warudo_character_animation` (`oneShot`) |
| 「右手がこの小道具に触れるように伸ばしてよ。」 | `warudo_character_ik_set` |
| 「この写真みたいなポーズを作ってよ。」(写真を添付) | `warudo_analysis_capture` → `warudo_character_pose_set` 繰り返し |
| 「衣装は全部オフにして、体だけを見せてよ。終わったらまた付けてね。」 | `warudo_hierarchy_list` → `warudo_object_active` |
| 「WARUDO画面をUIまで全部キャプチャしてよ。」 | `warudo_screen_capture` |
| 「さっきエラーが出たみたい。WARUDOのログを見て原因を探してよ。」 | `warudo_runtime_logs` |
| 「今のシーンを『配信用_テスト』という名前で別に保存してよ。」 | `warudo_scene_save_as` |
| 「シーンのリストを見せて、昨日使ってたシーンを開いてよ。」 | `warudo_scene_list` → `warudo_scene_open` |
| 「ブループリントでこのノードの値を変えてよ。」 | `warudo_blueprint_get` → `warudo_node_patch` |

全ツール66個は[docs/TOOLS.md](docs/TOOLS.md)にあります。

**リクエストのコツ**

- 結果を目で見たければ、**「撮ってみせてよ」** を付けてください。AIが写真を撮ってチャットに見せてくれます。
- 気に入らなければ、そのまま修正をお願いできます。「腕をもう少し上げて」「カメラを少し左に」。
- 一度に複数のリクエストをしてもいいです。「笑った顔で手を振って、正面から撮ってよ。」
- 終わったら**「元に戻してよ」** ひとことで表情・ポーズ・カメラが戻ります。

## 必要なもの

| 必要なもの | 説明 |
| --- | --- |
| Windows 10 または 11 PC | |
| [WARUDO](https://store.steampowered.com/app/2079120/WARUDO/) (Steam) | MOD のインストールは必要ありません。ブリッジはWARUDOのPlayground機能で動きます。 |
| AIアプリ1つ | Claude デスクトップアプリ、Claude Code、Codex、ChatGPT デスクトップアプリ、Cursor のいずれか。[接続方法](#aiアプリに接続する) |
| Node.js 20以上 | よくわからなくても大丈夫です。入っていなければ、インストーラーがインストールするか聞いてきます。 |

**AIアプリを先にインストールして、1回実行してから** ブリッジをインストールしてください。インストーラーがこのPCにあるAIアプリを見つけて、自動的に接続するからです。

## インストールする

### ステップ1：PowerShellを開く

- **Windows 11**: 画面の下の スタートボタン（Windowsロゴ）を**右クリック** → **ターミナル**
- **Windows 10**: スタートボタンを**右クリック** → **Windows PowerShell**

黒色（または青色）のウィンドウが開きます。管理者権限は必要ありません。

### ステップ2：インストールコマンドを貼り付ける

下の1行をコピーして、PowerShell ウィンドウに貼り付けて（`Ctrl+V` または右クリック）、`Enter`を押してください。

```powershell
irm https://raw.githubusercontent.com/StudioRaming/warudo-agent-bridge/main/install.ps1 | iex
```

このコマンドはこのリポジトリの[install.ps1](install.ps1)をダウンロードして実行します。何をするか先に見たければ、リンクを開いてください。

### ステップ3：質問が出たら答える

Node.js がなければこう聞きます。

```
Install Node.js LTS now with winget? [Y/n]
```

`Y`を入力して`Enter`を押してください。Windowsが「このアプリがデバイスに変更を加えることを許可しますか?」と聞いたら、**はい**をクリックします。インストールが終わって「Open a new PowerShell window and run the installer again」と出たら、PowerShell ウィンドウを閉じて新しく開いて、ステップ2をもう一度してください。

### ステップ4：結果を確認する

終わったらこんな画面が出ます（見つかったAIアプリによって行数が異なります）。

```
WARUDO Agent Bridge 0.2.0
  home:     C:\Users\YOU\.warudo-agent-bridge
  runtime:  C:\...\Warudo\Warudo_Data\StreamingAssets\Playground\WarudoAgentBridge\WarudoAgentBridge.cs
  claude-code     registered (user scope)
  codex           registered
  claude-desktop  registered Restart Claude Desktop to load it.

Next: start WARUDO. A running WARUDO loads the new bridge by itself once Playground recompiles (up to about 30 seconds).
Open a new agent session and ask it to call warudo_status. Check anything odd with: warudo-agent-bridge doctor
```

- `runtime:` の行はWARUDOにブリッジファイルを入れたという意味です。
- `registered` はそのAIアプリに接続登録したという意味です。使いたいアプリがリストにないなら、[AIアプリに接続する](#aiアプリに接続する)を見てください。
- 変更する設定ファイルは先に `%USERPROFILE%\.warudo-agent-bridge\backups`にバックアップされます。

### ステップ5：WARUDOとAIアプリを再起動する

1. WARUDOを起動します。既に起動していたなら、そのままにして**30秒くらい待ってください。** WARUDOが新しいファイルを自動で読み込みます。
2. AIアプリを**完全に閉じて** 再度起動します。（ウィンドウを閉じるだけではバックグラウンドに残る可能性があります。タスクバーの右下のトレイアイコンからも終了してください。）

これで[最初に試す](#最初に試す)に進めます。

## AIアプリに接続する

インストーラーが見つけたアプリには自動登録されているので、下は**確認方法**と**できなかったときの対処**です。

### Claude デスクトップアプリ

- 自動登録位置: `%APPDATA%\Claude\claude_desktop_config.json`
- 確認：アプリをトレイアイコンまで完全に終了して再度起動します。会話の入力欄左下の **+** ボタン → **Connectors** → **Manage connectors** に `warudo-agent-bridge` が見えたら接続されています。設定の**Developer** タブでも見えます。会話するときAIがツールを使おうとしたら、許可するか聞かれます。
- 接続がうまくいかなければ `%APPDATA%\Claude\logs\mcp-server-warudo-agent-bridge.log` に理由が記録されます。
- インストール結果に `claude-desktop` がなかったなら：アプリを1回実行した後、PowerShellで下を実行してください。

```powershell
npx --yes @studioraming/warudo-agent-bridge@latest install --clients claude-desktop
```

### Claude Code

- 自動登録: `claude mcp add -s user ...` (全フォルダで使えるようにユーザースコープで登録)
- 確認：新しいターミナルで `claude mcp list` を実行するか、Claude Code 内で `/mcp` を入力します。
- インストール結果に `manual` が出たなら、`claude` コマンドが見つからなかったということです。結果と一緒に出た `run:` コマンドをそのまま実行してください。

### Codex (CLI、IDE拡張)

- 自動登録位置: `%USERPROFILE%\.codex\config.toml` (長いキャプチャが途切れないように `tool_timeout_sec = 180` も一緒に入ります)
- 確認：`codex mcp list` を実行するか、Codex 内で `/mcp` を入力します。
- インストール結果に `codex` がなかったなら：

```powershell
npx --yes @studioraming/warudo-agent-bridge@latest install --clients codex
```

### ChatGPT

- **ChatGPT ウェブサイトとモバイルアプリの一般チャットでは使えません。** 一般チャットはインターネットに公開（HTTPS）されたMCPサーバーだけを接続できますが、このブリッジは自分のPC内でだけ動くからです。
- **ChatGPT デスクトップアプリ（Windows）なら使えます。** デスクトップアプリはCodex CLI・IDE拡張と同じ設定ファイル（`%USERPROFILE%\.codex\config.toml`）を共有して、インストーラーはここにブリッジを登録します。
  1. ChatGPT デスクトップアプリをインストールしてログインします。
  2. 上のCodexセクションの `--clients codex` コマンドを実行します（インストール結果に `codex` が既にあったなら飛ばしてください）。
  3. アプリの**設定 → MCP servers** で `warudo-agent-bridge` が見えるか確認して、見えたら**Restart** をクリックします。
  4. 入力欄に `/mcp` を入力すると、接続されたサーバーのリストが出ます。
- メニュー名と利用可能な料金プランはOpenAIが変更する可能性があります。2026年9月時点ではOpenAIドキュメント([MCP設定](https://learn.chatgpt.com/docs/extend/mcp?surface=app))に従いました。

### Cursor と他のアプリ

Cursor は `%USERPROFILE%\.cursor\mcp.json` に自動登録されます。他のMCPアプリは[手動登録](#手動登録)を見てください。

## 最初に試す

1. **WARUDOを起動して** キャラクターがあるシーンを開きます。
2. **AIアプリで新しい会話を開きます。** (インストール前に開いていた会話にはツールが見えない可能性があります。)
3. こう送ってみてください。

   > WARUDOと接続できてるか warudo_status で確認してよ。

   「接続できてます」のような返答と一緒に WARUDO バージョン・開いているシーン名が出たら成功です。
4. AIアプリが**ツールを使ってもいいか聞いたら** 内容を見て許可をクリックしてください。
5. あとは[例](#できること)を1つずつ送ってみてください。最初は「キャラクターを正面から撮ってみせてよ」が結果がすぐ見えるので良いです。

## 安全に使う

- **大事なシーンは先に保存してください。** または「今の状態のスナップショットを作ってよ」とお願いしてください。ブリッジもシーンを開く、スナップショット復帰、アセット削除の前には復帰用スナップショットを自動で残して、保存されたシーンを上書きするときは前のファイルを残します。
- **配信中は気を付けてください。** AIが変えたことはWARUDO画面にすぐに出ます。配信前にリハーサルで試してください。
- **一度に1つのAIだけがWARUDOを変えられます。** 2番目のAIはシーンを巡って争う代わりに「使用中」という返答をもらいます。
- **結果を盛りません。** 途中でWARUDOが落ちたら「未実行」または「要確認」と知らせて、結果が不確実な変更は自動的に再送信しません。
- **Unityスクリプト関数呼び出しはデフォルトでオフです。** 設定ファイルで許可したものだけが呼べます。

## よくある質問

**お金はかかりますか？**\
ブリッジは無料です。AIアプリの利用料は各サービスの料金プランに従います。

**コーディングを知らなくても大丈夫ですか？**\
はい。インストール時にコマンド1行を貼り付けることを除いて、チャットでお願いするだけです。

**WARUDOのMODをインストールする必要はありますか？**\
いいえ。WARUDOのPlayground フォルダにスクリプトファイル1つを入れるだけです。インストーラーが自動的に入れます。

**自分のキャラクターでも使えますか？**\
はい。VRMでも一般的なヒューマノイドアバターでも、ポーズツールがそのまま合います。表情の名前はキャラクターごとに異なりますが、AIが先にリストを読んで合ったものを選びます。

**日本語でお願いしても大丈夫ですか？**\
はい。AIアプリが理解できる言語なら何でもいいです。ツール名は英語でも、リクエストは日本語で大丈夫です。

**Macでも使えますか？**\
いいえ。WARUDOがWindows専用なので、ブリッジもWindows専用です。

**AIが変えたことは元に戻せますか？**\
表情・ポーズ・カメラは「元に戻してよ」ひとことで済みます。シーン全体は事前に作ったスナップショットで戻します。

**アップデートはどうすればいいですか？**\
[ステップ2](#ステップ2インストールコマンドを貼り付ける)のインストールコマンドを再度実行すると最新バージョンになります。

## 問題が起きたとき

| 症状 | 対処 |
| --- | --- |
| AIが「ブリッジが落ちている」「接続できない」と言う | WARUDOを起動して30秒待った後、もう一度お願いしてください。それでもダメなら、下の `doctor` を実行してみてください。 |
| AIアプリにツールが見えない | AIアプリをトレイアイコンまで完全に終了してから再度起動して、**新しい会話**を開いてください。インストール結果にそのアプリ名がなかったなら[AIアプリに接続する](#aiアプリに接続する)のコマンドを実行してください。 |
| `用語 'npx' は ... 認識されません` (または `'node'`) | Node.js をインストール直後なら、PowerShell ウィンドウを閉じて新しく開いた後、再度実行してください。 |
| `runtime: warudo-not-found` | Steamライブラリが他の場所にある場合です。`Warudo.exe` があるフォルダを教えてください： `npx --yes @studioraming/warudo-agent-bridge@latest install --warudo "D:\SteamLibrary\steamapps\common\Warudo"` |
| `runtime: NOT installed yet` | 古いバージョンのファイルが多いため、WARUDOの起動中は置き換えませんでした。WARUDOを閉じてインストールを再度実行してください。 |
| Codex でキャプチャ中にタイムアウト | インストールを再度実行すると `tool_timeout_sec = 180` が入ります。 |

状態を一度にチェックするには：

```powershell
npx --yes @studioraming/warudo-agent-bridge@latest doctor
```

`FAIL` の行が原因です（`INFO` の行は参考情報なので問題ではありません）。解決しなければその結果を貼り付けて[イシュー](https://github.com/StudioRaming/warudo-agent-bridge/issues)として報告してください。

## 詳細情報（上級者向け）

### インストールオプション

- `--clients claude-code,codex`(または `none`)：登録するAIアプリを選びます。
- `--warudo "D:\...\Warudo"`：Steamで WARUDO が見つからないときに場所を教えます。
- `--skip-runtime`：WARUDOには手を付けずにAIアプリ登録だけします。

1行インストールにオプションを付けるには、まず環境変数を設定します。

```powershell
$env:WARUDO_AGENT_BRIDGE_CLIENTS = "claude-code,codex"; irm https://raw.githubusercontent.com/StudioRaming/warudo-agent-bridge/main/install.ps1 | iex
```

### 手動登録

インストーラーがこのPC用のコマンドをそのまま出力してくれます。Claude Code なら、こんなふうです。

```bash
claude mcp add -s user warudo-agent-bridge -- node "C:\Users\YOU\.warudo-agent-bridge\app\bin\warudo-agent-bridge.mjs" mcp
```

JSON設定を使うクライアントは `{"mcpServers": {"warudo-agent-bridge": {"command": "node", "args": ["C:\\Users\\YOU\\.warudo-agent-bridge\\app\\bin\\warudo-agent-bridge.mjs", "mcp"]}}}`です。

### 仕組み

```
AI クライアント ──stdio──> MCP サーバー (Node) ──ファイル──> WARUDO内のランタイム (Playground C#)
                         %USERPROFILE%\.warudo-agent-bridge\runtime
```

- **ネットワークを使いません。** リクエストと返答はユーザーフォルダ内のファイルです。ポートを開きません。
- **書き込みは一度に1つのエージェント。** WARUDO を変えるツールには短い書き込みロック(lease)が必要です。MCPサーバーが自動で取得して、2番目のエージェントはシーンを巡って争う代わりに「使用中」という返答をもらいます。
- **結果を盛りません。** リクエストは実行前に受け取り表示をします。途中でWARUDOが落ちたりブリッジが再ロードされたら、`NOT_EXECUTED`(再送信しても安全) または `UNKNOWN`(状態を再度読んで確認)で返答します。読み取りと絶対値書き込みは再ロードを超えて1回自動で再試行します。
- **戻す道を残します。** シーンを開く、スナップショット復帰、アセット削除の前には復帰用スナップショットを保存して、保存されたシーンを上書きするときは前のファイルを残します。

### キャラクターポーズ

ポーズツールはUnity ヒューマノイド基準なので、VRM でも一般的なヒューマノイドアバターでもそのまま合います。

- `warudo_character_pose_set` はボーンの向き(`{"LeftUpperArm": {"aim": [-1, -0.2, 0]}}`, xは右・yは上・zは前)とマッスル値を受け取って、階層順序(親が先)で適用します。`base: "standing"` はアバター自身の基本姿勢から始まり、`hipsHeight` で骨盤高さをメートル単位で合わせます（大人の体形が膝をついて立つ姿勢は約0.35）。
- 関節はアバターの可動範囲を超えません。エイムが範囲を超えたら返答の `limited` に書き込むので、エージェントはそのエイムを減らせます。
- キャラクターにポーズが実際に見えた後に返答します(`shown`)。すぐに続いて撮るキャプチャが正確です。
- `warudo_analysis_capture` はマテリアルと背景なしで、部位ごとの色で体を複数の方向から一度にレンダーして、手の接触、部位ごとの床高さ、カメラ画面での各関節の位置を教えます。エージェントはシェーディングされたスクリーンショットを推測する代わりに、これでリファレンス画像とポーズを合わせます。
- `warudo_character_shot` はキャラクターの周りの軌道からキャラクターの写真を撮ります（キャラクターの向きを基準にした名前付きの視点、全身から顔までのフレーミング、最大6枚のコンタクトシート）。メインカメラのレンダリングをそのまま真似する一時カメラを使うので、ユーザーのカメラは動きません。視点ごとに、キャラクターが画面のどこにいるか、画面をどれだけ占めているか、ほかの物にどれだけ隠れているかを報告します。シーンに Character アセットがあるのに「キャラクターなし」になることはありません。エラーは、ロードされていないのか、無効なのか、レンダリングするものがないのかを伝えます。キャラクターの写真は `warudo_camera_capture` の代わりにこれで撮ります。
- `warudo_render_pass` は動作中のレンダラーの平坦な診断パス（parts, albedo, texture, alpha, normals, depth, uv, lighting, overlap）を複数の視点から描いて、ラベル付きの画像1枚で返します。凡例にはマテリアルスロットごとに、レンダラーのパス、スロット、マテリアル、シェーダー、カットアウトモード、実際に使われているテクスチャ（MaterialPropertyBlock のプラグインによる色変更がマテリアルより優先されます）、タイリング、カメラフレームの範囲が入ります。WARUDO では何も変わりません。`passes x views` は最大12パネルで、`texture` には `textureProperty` が必要です。
- `warudo_render_isolate` は選んだレンダラーの実際の見た目を見せます（`warudo_hierarchy_list` の `objectIds`、またはパスの一部の文字列 `include`）。`highlight` はフレームを残してほかを暗くし、`only` はそれだけをニュートラル、チェッカー、または透明の背景に表示します。パーツごとに画面上の範囲、見えている割合、平均 sRGB 色を返します。メインカメラを真似する一時カメラを使い、選んだレンダラーは数回の同期レンダリングのあいだ未使用のレイヤーに移され、同じ呼び出しの中で元に戻ります。
- `warudo_character_pose_save` はポーズをUnity ヒューマノイド `.anim` で保存します。WARUDO アニメーションリストに入れたり、好きなフォルダ（例：Unity プロジェクト）に書き込みます。
- `warudo_character_pose_clear` は元の待機アニメーションに戻します。

### 仮想ポインター（フォーカス不要）

エージェントは、WARUDO がバックグラウンドにあっても WARUDO のメインウィンドウの中でクリック、ダブルクリック、ドラッグ、ホバー、スクロールができます。ブリッジがフレーム単位の短いマウスのタイムラインを Unity の UI 入力モジュールに再生し、再生しているあいだ UI にウィンドウがフォーカスを持っていると伝えます。Windows のマウス・キーボードイベントは送らず、WARUDO を前面に出さず、実際のマウスも動かしません。

- **届く範囲：** メインウィンドウの UI（UGUI：ボタン、スライダー、トグル、スクロール領域、パネル）と、下の契約に従うプラグイン。
- **届かない範囲：** WARUDO のカメラ（実際のマウスを読むので、カメラツールを使ってください）、WARUDO エディタのウィンドウ（別のウィンドウ）、`Input.mousePosition` や `Input.GetKey` を直接読むプラグイン。
- **典型的な使い方：** `warudo_ui_list`（クリックできるものと矩形の座標）→ ラベルで `warudo_ui_click`、または1点に `warudo_pointer_click` / `warudo_pointer_drag` / `warudo_pointer_wheel`。`capture: true` はポインターを置いたまま撮ったスクリーンショットを返します。`warudo_ui_at` は1点を何が覆っているかを示し、覆われたコントロールを狙ったクリックは、上にあるものを押す代わりに `COVERED` で止まります。`warudo_ui_click` は `near`（見えている文字）、`mark`、`label`、`id`、または文字のないアイコンボタン用の `path`（`warudo_ui_list` で読んだ要素パスの一部。例：`btn:menu.undo`）を受け取ります。`warudo_pointer_wheel` はその点の下にあるものが何であってもホイールを回すので、パネルをスクロールするつもりのホイールがスライダーの値を変えることがあります。`expectScroll: true` を渡すと、スクロール領域がホイールを受け取らない場合は最初のノッチの前に `NOT_SCROLL_AREA` で止まり（エラーが何が受け取るかを伝えます）、`expectObject` を渡すとそのオブジェクトがポインターの下にない場合は `COVERED` で止まります。どちらも機能 `pointer-guards` が必要です。`warudo_ui_set_text` は入力フィールドを埋めます（入力した文字は送れないためです）。`warudo_pointer_state` は押されているものを示し、`warudo_pointer_release` はすべてを終わらせます。
- **安全：** 1つのジェスチャーは最大600フレーム、64点です。押したままのボタン（複数回の呼び出しにわたるドラッグ）や置いたままのポインターは、離したとき、書き込みの lease が終わったとき、または使われないまま `pointer.idleReleaseMs` が過ぎたときに終わります。再生中に WARUDO の中を自分でクリックすると、ジェスチャーは `USER_INPUT` で止まり、実際のマウスはまたユーザーのものになります。`pointer.enabled: false` ですべてオフにできます。
- **このバージョンのランタイムが必要です。** 古いランタイムがインストールされていると `RUNTIME_OUTDATED` で返答します。再インストールして、WARUDO に Playground を再コンパイルさせてください。

**プラグインの契約（WARUDO プラグインに仮想ポインターを見せるには）：**

```csharp
var module = EventSystem.current?.currentInputModule;
var input = module?.input;                        // a BaseInput; the bridge swaps it while a gesture plays
bool virtualInput = module?.inputOverride != null; // treat as focused: do not require Application.isFocused
// Mouse: input.mousePosition (bottom-left origin), input.GetMouseButton/Down/Up(0..2), input.mouseScrollDelta. Not UnityEngine.Input.
// Keys and modifiers: input has public bool GetKey/GetKeyDown/GetKeyUp(KeyCode) when virtual; find them by name (reflection or dynamic).
// Otherwise (inputOverride == null) read the real UnityEngine.Input as before.
```

### 読みやすいキャプチャ

スクリーンショットはエージェントが見るものなので、ツールごとに1種類の情報を見分けやすくしています。画像ツールはすべて、PNG、数値のテキスト、そしてマッピング（画像がウィンドウのどの領域を表し、どう縮小されたか）を持つ `imageId` を返します。

- `warudo_screen_capture`：ウィンドウ全体を `maxSize`（デフォルト1600）に合わせます。`region` [x, y, w, h] または `around` { objectId | path }（プラグインのオーバーレイパネルを含む UI オブジェクト、またはレンダラー）は、まず元の解像度で切り抜くので、小さな文字も読めます。`grid` はウィンドウのピクセル単位でラベルが付いた座標グリッドを重ねて描きます。
- `warudo_ui_capture`：`warudo_ui_list` が返すすべてのコントロールの上に番号付きの枠を描いた同じスクリーンショットです。種類（button, toggle, slider, input, scroll）ごとに色が違い、凡例がテキストで付きます。続けて `warudo_ui_click` は `mark`（番号。コントロールが変わっていれば `MARK_STALE` で拒否）または `near`（見えている文字。完全一致が先、次に部分一致）を受け取れます。
- ジェスチャーキャプチャ（ポインターツールや `warudo_ui_click` の `capture: true`）：画像にポインターが描かれます（押下、離す、ドラッグの軌跡、`markPointer`）。`captureBefore: true` は BEFORE と AFTER をPNG1枚に並べて返し、AFTER には変わった領域を枠で示し、差分の数値を付けます。
- `warudo_screen_diff`：今のウィンドウを以前のキャプチャ（`baseline` = キャプチャツールが返したパス）と比べます。変わっていないピクセルはグレー、変わったピクセルは色で表示され、枠に番号が付き、枠ごとに前後の平均色が出ます。
- `warudo_screen_probe`：テキストのみ返します。点や領域の平均、中央値、HSV、輝度、最小・最大 RGB で、画像からピクセルを読み取らなくても色を比べられます。
- `warudo_screen_sequence`：レンダリングされたKフレームごとに1枚、2〜16フレームを番号付きのコンタクトシート1枚で返し、各フレームが直前のフレームからどれだけ変わったかも伝えます（ホバー、アニメーション、ちらつきの確認）。
- `warudo_ui_language`：WARUDO のプラグインが読む言語（`en`, `ja`, `ko`, `zh_CN` など WARUDO が文字列を持つコード）を切り替えるので、WARUDO の設定を開かなくてもプラグインのメニューを言語ごとにキャプチャできます。ディスクには何も保存しません。`restore: true` は最初に変える前の言語に戻します。書き込みの lease が終わったとき、`warudo_pointer_release` を呼んだとき、ブリッジが止まったときは、ブリッジが自動で戻します（その間に WARUDO で別の言語を自分で選んだ場合は除く）。シーンの読み込み中は `SCENE_LOADING` で拒否します。機能 `ui-language`。
- **見えている場所をそのままクリック。** ポインターツール、`warudo_ui_at`、またはキャプチャツールの `region`/`points` に `space: "image"`（必要なら `imageId` も。デフォルトはこの MCP サーバーの最新の画像で、直近32枚を保持）を渡すと、x/y は受け取った画像のピクセルになります。サーバーがウィンドウのピクセルに変換します。画像の外の点、前後シートの AFTER パネルの外の点、コンタクトシート上の点は拒否されます。
- **このバージョンのランタイムが必要です**（機能 `capture-tools`）。`npm run check:pixels` は UnityEngine のスタブを使った通常の .NET 上でピクセル計算（切り抜き、縮小、差分の枠、色の統計）を実行します。`npm run check:runtime` と同じ .NET SDK と WARUDO のファイルが必要です。

### 設定

`%USERPROFILE%\.warudo-agent-bridge\config.json` ([config.example.json](config.example.json) から作られます)：

- `pointer`：`enabled`（デフォルト `true`）は仮想ポインターと `warudo_ui_set_text` のオン・オフです。`idleReleaseMs`（デフォルト15000、1000..600000）は、置いたままのポインターや押したままのボタンが次の呼び出しを待ってから離されるまでの時間です。
- `componentCall.allow`：エージェントが呼んでもいいUnity コンポーネントメソッド。`{"typePrefix": "My.Namespace.", "methods": ["Refresh"]}` という形式で書きます。デフォルトは空なので`warudo_component_call` はオフです。
- `retention`：返答、キャプチャ、スナップショット、ポーズプレビューを `warudo_maintenance_cleanup` が削除するまで保持する期間。

### コマンドライン

AIクライアントなしでも同じ機能を使えます。

```bash
npx @studioraming/warudo-agent-bridge status
npx @studioraming/warudo-agent-bridge call scene.get
npx @studioraming/warudo-agent-bridge tools
```

インストール後は `%USERPROFILE%\.warudo-agent-bridge\bin\warudo-agent.cmd` が同じコマンドラインのショートカットです。

### アンインストール

```bash
npx @studioraming/warudo-agent-bridge uninstall
```

WARUDO Playground からランタイムを外して（完全に取り外すにはWARUDO再起動）、クライアントのMCP項目をバックアップと一緒に削除します。`--purge` を付けたら `backups` フォルダだけ残して `%USERPROFILE%\.warudo-agent-bridge` も削除します。

### 開発

[CONTRIBUTING.md](CONTRIBUTING.md)を見てください。セキュリティ問題の報告は[SECURITY.md](SECURITY.md)に従ってください。

## ライセンス

[MIT](LICENSE) © StudioRaming
