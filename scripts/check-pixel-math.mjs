#!/usr/bin/env node
// Runs the pixel math of the capture tools (runtime/Pixmap.cs, runtime/PixelDiff.cs: crop, box-filter downscale, diff boxes and
// merge gap, color statistics) under plain .NET, next to a stub of the few UnityEngine types they touch (scripts/pixel-math/Stub.cs).
// Needs a .NET SDK and WARUDO's Newtonsoft.Json.dll (same requirements as scripts/compile-runtime.mjs). No WARUDO is started.
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { findWarudo } from '../src/warudo-locate.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const layout = findWarudo({ explicit: process.env.WARUDO_PATH });
const newtonsoft = layout && path.join(layout.managed, 'Newtonsoft.Json.dll');
if (!newtonsoft || !fs.existsSync(newtonsoft)) {
  console.error('WARUDO not found (Newtonsoft.Json.dll is taken from its Managed folder). Set WARUDO_PATH.');
  process.exit(2);
}

const csc = execFileSync('dotnet', ['--list-sdks'], { encoding: 'utf8' }).trim().split(/\r?\n/)
  .map((line) => line.match(/^(\S+)\s+\[(.+)\]$/)).filter(Boolean)
  .map(([, version, base]) => path.join(base, version, 'Roslyn', 'bincore', 'csc.dll')).filter((file) => fs.existsSync(file)).pop();
const runtimes = execFileSync('dotnet', ['--list-runtimes'], { encoding: 'utf8' }).split(/\r?\n/)
  .map((line) => line.match(/^Microsoft\.NETCore\.App (\d+)\.(\S+) \[(.+)\]$/)).filter(Boolean)
  .filter(([, major]) => Number(major) >= 6).map(([, major, rest, base]) => ({ major: Number(major), dir: path.join(base, `${major}.${rest}`) }));
if (!csc || !runtimes.length) {
  console.error('A .NET SDK (with Roslyn) and a .NET 6+ runtime are required.');
  process.exit(2);
}
const runtime = runtimes.sort((a, b) => a.major - b.major)[0];
const references = fs.readdirSync(runtime.dir).filter((name) => /^(System\..*|mscorlib|netstandard|WindowsBase)\.dll$/.test(name) && !/Native|Compat/.test(name)).map((name) => path.join(runtime.dir, name));
references.push(newtonsoft);

const work = fs.mkdtempSync(path.join(os.tmpdir(), 'wab-pixel-math-'));
try {
  const out = path.join(work, 'pixelmath.dll');
  const sources = [path.join(root, 'scripts', 'pixel-math', 'Stub.cs'), path.join(root, 'scripts', 'pixel-math', 'Program.cs'), path.join(root, 'runtime', 'Pixmap.cs'), path.join(root, 'runtime', 'PixelDiff.cs')];
  const rsp = path.join(work, 'build.rsp');
  fs.writeFileSync(rsp, ['/nologo', '/target:exe', '/langversion:9.0', '/nowarn:1701,1702', `/out:"${out}"`, ...references.map((file) => `/r:"${file}"`), ...sources.map((file) => `"${file}"`)].join('\n'));
  const build = spawnSync('dotnet', ['exec', csc, `@${rsp}`], { encoding: 'utf8' });
  const errors = `${build.stdout}${build.stderr}`.split(/\r?\n/).filter((line) => /error/.test(line));
  if (build.status !== 0) {
    console.error(errors.join('\n') || 'compile failed');
    process.exit(1);
  }
  fs.copyFileSync(newtonsoft, path.join(work, 'Newtonsoft.Json.dll'));
  fs.writeFileSync(path.join(work, 'pixelmath.runtimeconfig.json'), JSON.stringify({ runtimeOptions: { tfm: `net${runtime.major}.0`, framework: { name: 'Microsoft.NETCore.App', version: `${runtime.major}.0.0` } } }));
  const run = spawnSync('dotnet', ['exec', out], { encoding: 'utf8', cwd: work });
  process.stdout.write(run.stdout);
  process.stderr.write(run.stderr);
  process.exit(run.status ?? 1);
} finally {
  fs.rmSync(work, { recursive: true, force: true });
}
