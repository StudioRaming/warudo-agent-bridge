# Security

## Reporting a vulnerability

Please report security issues privately through GitHub: **Security > Report a vulnerability** on
[StudioRaming/warudo-agent-bridge](https://github.com/StudioRaming/warudo-agent-bridge/security/advisories/new).
Do not open a public issue for them. You will get an answer within a week.

## Supported versions

Only the latest release gets fixes.

## What the bridge trusts

- **Local only.** The MCP server and the WARUDO runtime talk through files in
  `%USERPROFILE%\.warudo-agent-bridge`. No port is opened. Any program that runs as your Windows user
  can already write there, so the bridge gives such a program no more than your own account has.
- **The agent is not trusted blindly.** Every tool that changes WARUDO needs the write lease, one
  agent at a time. Deleting an asset needs the agent's own readback of it, overwriting a saved scene
  needs `allowOverwrite` and keeps the old file, and opening a scene, restoring a snapshot or deleting
  an asset saves a recovery snapshot first.
- **No arbitrary code.** Unity component methods can be called only when you list them in
  `config.json` under `componentCall.allow` (empty by default). Asset sources must be URIs WARUDO
  lists or files inside WARUDO's own data folders. Captures are written only to the bridge's evidence
  folder, and only PNG files from there are read back.
- **Logs.** Values that look like tokens, passwords or keys are redacted before logs reach the agent.
