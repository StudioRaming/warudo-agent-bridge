# Changelog

## Unreleased

### Fixed
- Check resolved screenshot evidence paths before reading PNGs, rejecting file symlinks or directory junctions that point outside the evidence directory.

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
