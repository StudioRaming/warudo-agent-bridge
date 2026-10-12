# Changelog

## Unreleased

### Plugin settings (Tools: 66)
- `warudo_plugin_list`, `warudo_plugin_fields` and `warudo_plugin_patch` read and change what WARUDO's Settings tab shows (plugin data inputs such as `Warudo.NiloToon` Enabled or `Warudo.Core` RenderResolutionMultiply). Same validation, rollback and before/after contract as the asset tools.
- A patch is applied live and saved through `PluginManager.SavePluginData`, the call the Settings tab uses (`Plugins/Data/<id>.json`). The reply reports `persisted`, the render pipeline before and after (NiloToon Enabled switches URP and Built-in), and `restartReported: false` because WARUDO exposes no restart signal for plugin settings.
- `warudo_trigger_invoke` also presses plugin trigger buttons (`pluginId`).
- The runtime reports the `plugin-settings` feature, so a newer MCP server can tell whether the installed runtime has these operations.

### Fixed
- Check resolved screenshot evidence paths before reading PNGs, rejecting file symlinks or directory junctions that point outside the evidence directory.
- Validate install/uninstall client selections before changing files, runtime installation, or client registrations. Unknown client names now fail before any external effects.
- Reject a missing CLI option value before it can consume another flag such as `--skip-runtime`; dash-leading literal values can use `--name=VALUE`.
- Preserve unrelated Codex TOML settings when comments or single-line strings contain triple quotes; correctly recognize escaped and four/five-quote multiline string endings during bridge registration/removal.

## 0.2.0 - 2026-09-28

First public release.

### Connection
- File mailbox between the MCP server and a runtime that WARUDO hot-loads from its Playground folder: nothing listens on the network.
- Protocol 2: a request is claimed before it runs, so a reload or a closed WARUDO is reported as `NOT_EXECUTED` (safe to retry) or `UNKNOWN` (read the state back), never guessed.
- Reads and absolute-value writes retry once across a runtime reload; the heartbeat is re-read when WARUDO replaces it mid-write. A changed install reloads the runtime once and the write lease survives it.
- Pose changes return only once the character shows them.

### Tools (63)
- Scene: read, open, save-as, snapshots and restore.
- Assets: list, fields, patch, add, delete (with recovery snapshot), transform, source loading with a wait.
- Characters: expressions, blendshape overrides that restore cleanly, idle and one-shot animations, humanoid posing (muscles or bone aims in body or character space, `hipsHeight` in meters, joints held inside the avatar's range and reported), IK targets, saving a pose as a Unity `.anim`.
- Blueprints: list, read, enable, read and patch node fields, run a node's flow.
- Camera: orbit, pan, zoom, pose, restore, capture; screen capture; analysis renders (clay, skeleton, wireframe, several views, contacts, floor clearance, screen joints for matching a reference image).
- Unity objects: hierarchy, transforms, active state, component read and patch, shader globals, viewport picking; component method calls only through the `componentCall.allow` list in `config.json`.
- Resources, hotkey events (only while WARUDO has focus), runtime logs (secrets redacted), maintenance cleanup of old bridge files.

### Install
- `install`, `doctor` and `uninstall` commands; `install.ps1` for a one-line setup that also offers Node.js LTS through winget.
- Registers the MCP server with Claude Code, Codex, Claude Desktop and Cursor (auto-detected, or `--clients`), keeping a backup of every config file it edits.
- Unknown command-line options stop the command instead of being ignored.
