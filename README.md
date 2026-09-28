# WARUDO Agent Bridge

**English** | [한국어](README.ko.md) | [日本語](README.ja.md)

Let AI agents see and drive a running [WARUDO](https://warudo.app): Claude Code, Codex, Claude
Desktop, Cursor or any other [MCP](https://modelcontextprotocol.io) client.

Ask in plain words, the agent does the clicking:

- "Show me what's in the scene and take a picture of the character from the front."
- "Pose her like this photo, kneeling, and save the pose as an .anim in my Unity project."
- "Turn off every mesh except the body, then put everything back."
- "Switch the idle animation, zoom the camera to the face and capture it."

63 tools cover the scene, assets, characters (pose, IK, expressions, blendshapes, animations),
blueprints, the camera and captures, and Unity objects. See [docs/TOOLS.md](docs/TOOLS.md).

> Not affiliated with WARUDO or HakuyaLabs. MIT licensed, made by StudioRaming.

## Requirements

- Windows 10 or 11
- WARUDO (Steam). The bridge runs inside it as a Playground script, no mod install needed.
- Node.js 20 or newer (the installer offers to install it)

## Install

In PowerShell:

```powershell
irm https://raw.githubusercontent.com/StudioRaming/warudo-agent-bridge/main/install.ps1 | iex
```

Or, with Node.js already installed:

```bash
npx --yes @studioraming/warudo-agent-bridge@latest install
```

The installer:

1. copies the bridge to `%USERPROFILE%\.warudo-agent-bridge`,
2. finds WARUDO through Steam and puts one runtime file in
   `Warudo_Data\StreamingAssets\Playground\WarudoAgentBridge\` (a running WARUDO loads it by itself;
   Playground recompiles, which takes a few to about 25 seconds),
3. registers the MCP server with every client it finds: Claude Code, Codex, Claude Desktop, Cursor.
   Each config file it edits is backed up under `%USERPROFILE%\.warudo-agent-bridge\backups` first.

Then restart your AI client and ask it to call `warudo_status`.

Options: `--clients claude-code,codex` (or `none`) picks the clients, `--warudo "D:\...\Warudo"`
points at WARUDO when Steam lookup fails, `--skip-runtime` only registers the clients.
Check a setup with `npx @studioraming/warudo-agent-bridge doctor`.

### Register by hand

The installer prints the exact command for your machine. For Claude Code it looks like this:

```bash
claude mcp add -s user warudo-agent-bridge -- node "C:\Users\YOU\.warudo-agent-bridge\app\bin\warudo-agent-bridge.mjs" mcp
```

JSON clients use `{"mcpServers": {"warudo-agent-bridge": {"command": "node", "args": ["C:\\Users\\YOU\\.warudo-agent-bridge\\app\\bin\\warudo-agent-bridge.mjs", "mcp"]}}}`.

## How it works

```
AI client ──stdio──> MCP server (Node) ──files──> runtime inside WARUDO (Playground C#)
                     %USERPROFILE%\.warudo-agent-bridge\runtime
```

- **No network.** Requests and replies are files in your user folder. Nothing listens on a port.
- **One writer at a time.** Tools that change WARUDO need a short write lease. The MCP server takes it
  for you; a second agent gets a clear "busy" answer instead of fighting over the scene.
- **Honest results.** A request is claimed before it runs. If WARUDO closes or reloads the bridge,
  the reply says `NOT_EXECUTED` (safe to send again) or `UNKNOWN` (read the state back). Reads and
  absolute-value writes are retried once across a reload on their own.
- **Undo paths.** Opening a scene, restoring a snapshot and deleting an asset save a recovery
  snapshot first; overwriting a saved scene keeps the old file.

## Posing a character

The pose tools work in Unity's humanoid terms, so they fit any VRM or humanoid avatar.

- `warudo_character_pose_set` takes bone aims (`{"LeftUpperArm": {"aim": [-1, -0.2, 0]}}`, x right,
  y up, z forward) and/or muscles, applied parent-first down the hierarchy. `base: "standing"` starts
  from the avatar's own rest pose; `hipsHeight` sets the hips in meters (about 0.35 kneeling upright).
- Joints stay inside the avatar's range. When an aim asks for more, the reply lists it under
  `limited` so the agent can ease that aim.
- The reply comes once the character shows the pose (`shown`), so a capture right after is correct.
- `warudo_analysis_capture` renders the body without materials or background, colored by body part,
  from several views at once, and reports hand contacts, how high each body part is above the floor,
  and where every joint lands in the camera frame. Agents use it to check a pose against a reference
  image instead of guessing from a shaded screenshot.
- `warudo_character_pose_save` writes the pose as a Unity humanoid `.anim`, into WARUDO's own
  animation list or to any folder (for example a Unity project).
- `warudo_character_pose_clear` puts the previous idle animation back.

## Configuration

`%USERPROFILE%\.warudo-agent-bridge\config.json` (created from [config.example.json](config.example.json)):

- `componentCall.allow`: Unity component methods the agent may call, as
  `{"typePrefix": "My.Namespace.", "methods": ["Refresh"]}` entries. Empty by default, so
  `warudo_component_call` is off.
- `retention`: how long replies, captures, snapshots and pose previews are kept before
  `warudo_maintenance_cleanup` removes them.

## Command line

The same operations are available without an AI client:

```bash
npx @studioraming/warudo-agent-bridge status
npx @studioraming/warudo-agent-bridge call scene.get
npx @studioraming/warudo-agent-bridge tools
```

After install, `%USERPROFILE%\.warudo-agent-bridge\bin\warudo-agent.cmd` is a shortcut to the same CLI.

## Uninstall

```bash
npx @studioraming/warudo-agent-bridge uninstall
```

This removes the runtime from WARUDO's Playground (restart WARUDO to unload it) and the MCP entries
from the clients, with backups. Add `--purge` to also delete `%USERPROFILE%\.warudo-agent-bridge`
except its `backups` folder.

## Troubleshooting

- **`warudo_status` says the bridge is not running**: start WARUDO, wait for Playground to finish
  compiling, then `doctor`.
- **WARUDO not found**: pass `--warudo` with the folder that contains `Warudo.exe`, or set `WARUDO_PATH`.
- **Codex stops long calls**: the installer sets `tool_timeout_sec = 180` for this server; re-run
  the installer if your config predates it.
- **Something else**: `warudo_runtime_logs` shows WARUDO's recent log, and the issue tracker is at
  [github.com/StudioRaming/warudo-agent-bridge/issues](https://github.com/StudioRaming/warudo-agent-bridge/issues).

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md). Security reports: [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © StudioRaming
