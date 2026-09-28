# Contributing

Issues and pull requests are welcome. For anything larger than a fix, open an issue first so we can
agree on the shape.

## Setup

- Windows, Node.js 20+, and WARUDO (Steam) for live testing.
- Any .NET SDK, only for `npm run check:runtime`.

```bash
npm test                # unit and contract tests (no WARUDO needed)
npm run docs:tools      # regenerate docs/TOOLS.md after changing src/tools.mjs
npm run check:runtime   # compile runtime/*.cs against your WARUDO's assemblies (about 10 s)
```

## How the code is laid out

- `src/tools.mjs` defines every MCP tool: name, description, operation, kind and JSON schema.
- `runtime/*.cs` is the WARUDO side. The installer bundles the files into one
  `WarudoAgentBridge.cs` in WARUDO's Playground folder, which WARUDO compiles and hot-reloads.
- `tests/contract.test.mjs` keeps the two sides in step: operations, lease rules and versions.

## Rules

- A tool that changes WARUDO must need the lease; say in its description what it changes.
- Replies report what actually happened (read it back), not what was requested.
- Test live on a copy of a scene, never on your only copy. `warudo_scene_snapshot` first.
- Keep `CHANGELOG.md` up to date in the same pull request.
