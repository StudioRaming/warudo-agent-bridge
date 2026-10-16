// Compiles a few runtime/*.cs files plus a test Program.cs with Roslyn and runs them under plain .NET, with no WARUDO started.
// Shared by scripts/check-pixel-math.mjs and scripts/check-language-restore.mjs.
// Exit codes of runCSharp's result: 0 passed, 1 failed, 2 the environment lacks what it needs (a .NET SDK, or WARUDO for Newtonsoft.Json).
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { findWarudo } from '../../src/warudo-locate.mjs';

export function runCSharp({ name, sources, newtonsoft = false }) {
  let newtonsoftDll = null;
  if (newtonsoft) {
    const layout = findWarudo({ explicit: process.env.WARUDO_PATH });
    newtonsoftDll = layout && path.join(layout.managed, 'Newtonsoft.Json.dll');
    if (!newtonsoftDll || !fs.existsSync(newtonsoftDll)) {
      return { status: 2, message: 'WARUDO not found (Newtonsoft.Json.dll is taken from its Managed folder). Set WARUDO_PATH.' };
    }
  }

  let csc, runtimes;
  try {
    csc = execFileSync('dotnet', ['--list-sdks'], { encoding: 'utf8' }).trim().split(/\r?\n/)
      .map((line) => line.match(/^(\S+)\s+\[(.+)\]$/)).filter(Boolean)
      .map(([, version, base]) => path.join(base, version, 'Roslyn', 'bincore', 'csc.dll')).filter((file) => fs.existsSync(file)).pop();
    runtimes = execFileSync('dotnet', ['--list-runtimes'], { encoding: 'utf8' }).split(/\r?\n/)
      .map((line) => line.match(/^Microsoft\.NETCore\.App (\d+)\.(\S+) \[(.+)\]$/)).filter(Boolean)
      .filter(([, major]) => Number(major) >= 6).map(([, major, rest, base]) => ({ major: Number(major), dir: path.join(base, `${major}.${rest}`) }));
  } catch {
    return { status: 2, message: 'dotnet was not found.' };
  }
  if (!csc || !runtimes.length) return { status: 2, message: 'A .NET SDK (with Roslyn) and a .NET 6+ runtime are required.' };
  const runtime = runtimes.sort((a, b) => a.major - b.major)[0];
  const references = fs.readdirSync(runtime.dir).filter((file) => /^(System\..*|mscorlib|netstandard|WindowsBase)\.dll$/.test(file) && !/Native|Compat/.test(file)).map((file) => path.join(runtime.dir, file));
  if (newtonsoftDll) references.push(newtonsoftDll);

  const work = fs.mkdtempSync(path.join(os.tmpdir(), `wab-${name}-`));
  try {
    const out = path.join(work, `${name}.dll`);
    const rsp = path.join(work, 'build.rsp');
    fs.writeFileSync(rsp, ['/nologo', '/target:exe', '/langversion:9.0', '/nowarn:1701,1702', `/out:"${out}"`, ...references.map((file) => `/r:"${file}"`), ...sources.map((file) => `"${file}"`)].join('\n'));
    const build = spawnSync('dotnet', ['exec', csc, `@${rsp}`], { encoding: 'utf8' });
    if (build.status !== 0) {
      const errors = `${build.stdout}${build.stderr}`.split(/\r?\n/).filter((line) => /error/.test(line));
      return { status: 1, message: errors.join('\n') || 'compile failed' };
    }
    if (newtonsoftDll) fs.copyFileSync(newtonsoftDll, path.join(work, 'Newtonsoft.Json.dll'));
    fs.writeFileSync(path.join(work, `${name}.runtimeconfig.json`), JSON.stringify({ runtimeOptions: { tfm: `net${runtime.major}.0`, framework: { name: 'Microsoft.NETCore.App', version: `${runtime.major}.0.0` } } }));
    const run = spawnSync('dotnet', ['exec', out], { encoding: 'utf8', cwd: work });
    return { status: run.status ?? 1, stdout: run.stdout, stderr: run.stderr };
  } finally {
    fs.rmSync(work, { recursive: true, force: true });
  }
}

export function finish(result) {
  if (result.message) console.error(result.message);
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  process.exit(result.status);
}
