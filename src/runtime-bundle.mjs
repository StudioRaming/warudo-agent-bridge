import fs from 'node:fs';
import path from 'node:path';

export const RUNTIME_FILE = 'WarudoAgentBridge.cs';

// WARUDO recompiles the whole Playground once per *.cs file event and does not coalesce
// events. Shipping the runtime as one file means an update costs exactly one reload.
// The repository keeps the readable split files; this joins them deterministically.
export function bundleRuntime(runtimeDir, version) {
  const files = fs.readdirSync(runtimeDir).filter((name) => name.endsWith('.cs')).sort((a, b) => {
    if (a === 'AgentBridgePlugin.cs') return -1;
    if (b === 'AgentBridgePlugin.cs') return 1;
    return a.localeCompare(b, 'en');
  });
  const usings = new Set();
  const bodies = [];
  for (const name of files) {
    const text = fs.readFileSync(path.join(runtimeDir, name), 'utf8').replace(/^﻿/, '').replace(/\r\n/g, '\n');
    const lines = text.split('\n');
    const start = lines.findIndex((line) => /^namespace\s/.test(line));
    if (start < 0) throw new Error(`${name} has no namespace block`);
    for (const line of lines.slice(0, start)) {
      const match = line.match(/^using\s+[A-Za-z0-9_.]+\s*;\s*$/);
      if (match) usings.add(line.trim());
      else if (line.trim() && !line.trim().startsWith('//')) throw new Error(`${name}: only using directives may precede the namespace (found "${line.trim()}")`);
    }
    bodies.push(`// ---- ${name} ----\n${lines.slice(start).join('\n').trimEnd()}\n`);
  }
  const ordered = [...usings].sort((a, b) => {
    const system = (value) => (/^using System[;.]/.test(value) ? 0 : 1);
    return system(a) - system(b) || a.localeCompare(b, 'en');
  });
  return [
    `// WARUDO Agent Bridge runtime ${version ?? ''}`.trimEnd(),
    '// https://github.com/StudioRaming/warudo-agent-bridge (MIT License)',
    '// Generated from runtime/*.cs by the installer. Edit those files, not this one.',
    '',
    ...ordered,
    '',
    bodies.join('\n'),
  ].join('\n').replace(/\n/g, '\r\n');
}
