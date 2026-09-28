# WARUDO Agent Bridge

[English](README.md) | [한국어](README.ko.md) | **日本語**

実行中の [WARUDO](https://warudo.app) をAIエージェントが見て操作できるようにします。Claude Code、Codex、Claude Desktop、Cursorをはじめとした [MCP](https://modelcontextprotocol.io) クライアントで使えます。

言葉で頼むと、クリックはエージェントが行います。

- "シーンに何があるか見せて、キャラクターを正面から撮って。"
- "この写真のように膝をついた姿勢にして、Unityプロジェクトに .animで保存して。"
- "ボディだけ残して、メッシュを全部オフにして。終わったら元に戻して。"
- "待機アニメーションを変えて、カメラを顔に寄せてキャプチャして。"

63個のツールで、シーン、アセット、キャラクター(姿勢・IK・表情・ブレンドシェイプ・アニメーション)、ブループリント、カメラとキャプチャ、Unityオブジェクトを扱います。リストは [docs/TOOLS.md](docs/TOOLS.md) にあります。

> WARUDO・HakuyaLabsとは無関係の非公式ツールです。MITライセンス、StudioRaming製。

## 必要なもの

- Windows 10または11
- WARUDO (Steam)。ブリッジはWARUDO内でPlaygroundスクリプトとして実行されるため、モッドのインストールは不要です。
- Node.js 20以上 (なければインストールスクリプトがインストールを提案します)

## インストール

PowerShellで：

```powershell
irm https://raw.githubusercontent.com/StudioRaming/warudo-agent-bridge/main/install.ps1 | iex
```

Node.jsがすでにインストール済みの場合：

```bash
npx --yes @studioraming/warudo-agent-bridge@latest install
```

インストーラーの動作：

1. ブリッジを `%USERPROFILE%\.warudo-agent-bridge` にコピーします。
2. SteamからWARUDOを見つけて、`Warudo_Data\StreamingAssets\Playground\WarudoAgentBridge\` にランタイムファイル1つを配置します。実行中のWARUDOが自動で読み込みます（Playgroundの再コンパイルに数秒から約25秒かかります）。
3. 見つかったすべてのクライアント(Claude Code、Codex、Claude Desktop、Cursor)にMCPサーバーを登録します。編集する設定ファイルはまず `%USERPROFILE%\.warudo-agent-bridge\backups` にバックアップされます。

その後、AIクライアントを再起動して、`warudo_status` を呼び出すよう指示してください。

オプション: `--clients claude-code,codex`(または `none`)で登録するクライアントを選択し、Steamで見つからない場合は `--warudo "D:\...\Warudo"` でパスを指定し、`--skip-runtime` はクライアント登録のみを行います。セットアップ状態は `npx @studioraming/warudo-agent-bridge doctor` で確認できます。

### 手動登録

インストーラーは、このPCに適したコマンドを出力します。Claude Codeの場合は次のようになります。

```bash
claude mcp add -s user warudo-agent-bridge -- node "C:\Users\YOU\.warudo-agent-bridge\app\bin\warudo-agent-bridge.mjs" mcp
```

JSON設定を使用するクライアントは `{"mcpServers": {"warudo-agent-bridge": {"command": "node", "args": ["C:\\Users\\YOU\\.warudo-agent-bridge\\app\\bin\\warudo-agent-bridge.mjs", "mcp"]}}}` です。

## 動作方式

```
AIクライアント ──stdio──> MCPサーバー (Node) ──ファイル──> WARUDO内のランタイム (Playground C#)
                            %USERPROFILE%\.warudo-agent-bridge\runtime
```

- **ネットワークを使用しません。** リクエストと応答はユーザーフォルダ内のファイルです。ポートを開きません。
- **書き込みは一度に1エージェント。** WARUDOを変更するツールには短い書き込みロック(lease)が必要です。MCPサーバーが自動的に取得し、2番目のエージェントはシーンを巡って争うのではなく、「使用中」という返答を受け取ります。
- **正直な結果。** リクエストは実行前に受け取りを記録します。WARUDOが閉じるか、ブリッジが再ロードされると、`NOT_EXECUTED`(安全に再送信可)または `UNKNOWN`(状態を再度読み込んで確認)で応答します。読み取りと絶対値書き込みは、再ロード時に1回自動的に再試行されます。
- **戻る道を残します。** シーンを開く前、スナップショットを復元する前、アセットを削除する前に、復旧用スナップショットを保存します。保存されたシーンを上書きするときは、以前のファイルを保持します。

## キャラクターの姿勢

姿勢ツールはUnityのヒューマノイド基準なので、VRMでも通常のヒューマノイドアバターでもそのまま機能します。

- `warudo_character_pose_set` はボーンのエイム(`{"LeftUpperArm": {"aim": [-1, -0.2, 0]}}`, xは右・yは上・zは前)とマッスル値を受け、階層順(親優先)で適用します。`base: "standing"` はアバター自身の基本姿勢から開始し、`hipsHeight` で腰の高さをメートル単位で設定します(膝をついて立つ姿勢は約0.35)。
- 関節はアバターの可動範囲を超えません。エイムが範囲を超える場合、応答の `limited` にリストされるため、エージェントはそのエイムを減らすことができます。
- キャラクターが実際に姿勢を示した後に応答します(`shown`)。すぐ後に撮影するキャプチャが正確です。
- `warudo_analysis_capture` はマテリアルと背景なしで、ボディパート色でボディを複数の角度から一度にレンダーし、手の接触、部位ごとの床からの高さ、カメラフレーム内の各関節の位置を報告します。エージェントはシェーディングされたスクリーンショットを推測する代わりに、これを使ってリファレンス画像と姿勢を照合します。
- `warudo_character_pose_save` は姿勢をUnityヒューマノイド `.anim` として保存します。WARUDOアニメーションリストに追加するか、任意のフォルダ(例：Unityプロジェクト)に書き込みます。
- `warudo_character_pose_clear` は元の待機アニメーションに戻します。

## 設定

`%USERPROFILE%\.warudo-agent-bridge\config.json` ([config.example.json](config.example.json) から作成)：

- `componentCall.allow`: エージェントが呼び出せるUnityコンポーネントメソッド。`{"typePrefix": "My.Namespace.", "methods": ["Refresh"]}` の形式で指定します。デフォルトは空で、`warudo_component_call` はオフです。
- `retention`: 応答、キャプチャ、スナップショット、姿勢プレビューが `warudo_maintenance_cleanup` によって削除されるまで保持される期間。

## コマンドライン

AIクライアントなしでも同じ機能が使用できます。

```bash
npx @studioraming/warudo-agent-bridge status
npx @studioraming/warudo-agent-bridge call scene.get
npx @studioraming/warudo-agent-bridge tools
```

インストール後、`%USERPROFILE%\.warudo-agent-bridge\bin\warudo-agent.cmd` が同じコマンドラインのショートカットです。

## アンインストール

```bash
npx @studioraming/warudo-agent-bridge uninstall
```

WARUDOのPlaygroundからランタイムを削除し(完全にアンロードするにはWARUDOを再起動)、クライアントのMCPエントリをバックアップとともに削除します。`--purge` を追加すると、`backups` フォルダのみを残して `%USERPROFILE%\.warudo-agent-bridge` も削除します。

## トラブルシューティング

- **`warudo_status` がブリッジが実行されていないと言う場合**: WARUDOを起動し、Playgroundのコンパイルが完了するまで待ってから `doctor` を実行してください。
- **WARUDOが見つからない場合**: `Warudo.exe` があるフォルダを `--warudo` で指定するか、`WARUDO_PATH` を設定してください。
- **Codexが長い呼び出しを切断する場合**: インストーラーがこのサーバーに `tool_timeout_sec = 180` を設定します。古い設定の場合は、インストーラーをもう一度実行してください。
- **その他の問題**: `warudo_runtime_logs` でWARUDOの最近のログが表示でき、質問は [github.com/StudioRaming/warudo-agent-bridge/issues](https://github.com/StudioRaming/warudo-agent-bridge/issues) にお願いします。

## 開発

[CONTRIBUTING.md](CONTRIBUTING.md) をご覧ください。セキュリティ問題の報告は [SECURITY.md](SECURITY.md) に従ってください。

## ライセンス

[MIT](LICENSE) © StudioRaming
