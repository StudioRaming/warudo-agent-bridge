#!/usr/bin/env node
// Compiles the runtime against the installed WARUDO's own assemblies, the references
// Playground uses, so errors show up in seconds instead of inside WARUDO.
// Needs WARUDO and any .NET SDK (for Roslyn's csc.dll).
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { findWarudo } from '../src/warudo-locate.mjs';
import { bundleRuntime, RUNTIME_FILE } from '../src/runtime-bundle.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const warudoArg = process.argv.indexOf('--warudo');
const layout = findWarudo({ explicit: warudoArg > 0 ? process.argv[warudoArg + 1] : undefined });
if (!layout || !fs.existsSync(layout.managed)) {
  console.error('WARUDO not found. Pass --warudo "<WARUDO install folder>" or set WARUDO_PATH.');
  process.exit(2);
}

function findCsc() {
  const sdks = execFileSync('dotnet', ['--list-sdks'], { encoding: 'utf8' }).trim().split(/\r?\n/)
    .map((line) => line.match(/^(\S+)\s+\[(.+)\]$/)).filter(Boolean)
    .map(([, version, base]) => path.join(base, version, 'Roslyn', 'bincore', 'csc.dll'))
    .filter((file) => fs.existsSync(file));
  if (!sdks.length) throw new Error('No .NET SDK with Roslyn found (install any .NET SDK).');
  return sdks[sdks.length - 1];
}

// Managed assemblies only: native plugins in the folder have no PE "MZ" header.
const references = fs.readdirSync(layout.managed)
  .filter((name) => name.endsWith('.dll'))
  .map((name) => path.join(layout.managed, name))
  .filter((file) => {
    const header = Buffer.alloc(2);
    const fd = fs.openSync(file, 'r');
    try { fs.readSync(fd, header, 0, 2, 0); } finally { fs.closeSync(fd); }
    return header.toString('latin1') === 'MZ';
  });

const csc = findCsc();

function compile(label, sources) {
  const out = path.join(os.tmpdir(), `warudo-agent-bridge-check-${process.pid}.dll`);
  const rsp = path.join(os.tmpdir(), `warudo-agent-bridge-check-${process.pid}.rsp`);
  fs.writeFileSync(rsp, [
    '/nologo', '/preferreduilang:en-US', '/noconfig', '/nostdlib+', '/target:library', '/langversion:9.0', '/warn:4', '/nowarn:1701,1702',
    '/define:UNITY_2021_3_OR_NEWER', `/out:"${out}"`,
    ...references.map((file) => `/r:"${file}"`),
    ...sources.map((file) => `"${file}"`),
  ].join('\n'));
  const started = Date.now();
  const result = spawnSync('dotnet', ['exec', csc, `@${rsp}`], { encoding: 'utf8' });
  fs.rmSync(rsp, { force: true });
  fs.rmSync(out, { force: true });
  const lines = `${result.stdout}${result.stderr}`.split(/\r?\n/).filter((line) => line.trim());
  const errors = lines.filter((line) => / error /.test(line));
  const warnings = lines.filter((line) => / warning /.test(line) && !/CS1701|CS1702|CS8019/.test(line));
  for (const line of [...errors, ...warnings]) console.log(line.replace(root + path.sep, ''));
  console.log(`${result.status === 0 ? 'OK' : 'FAILED'} ${label}: ${sources.length} file(s), ${errors.length} errors, ${warnings.length} warnings, ${Date.now() - started} ms`);
  return result.status === 0;
}

// The split files give readable line numbers; the bundle is what WARUDO actually compiles.
const runtimeDir = path.join(root, 'runtime');
const split = fs.readdirSync(runtimeDir).filter((name) => name.endsWith('.cs')).map((name) => path.join(runtimeDir, name));
let ok = compile('runtime/*.cs', split);
if (ok) {
  const bundlePath = path.join(os.tmpdir(), `${process.pid}-${RUNTIME_FILE}`);
  fs.writeFileSync(bundlePath, bundleRuntime(runtimeDir, 'check'));
  ok = compile(`bundle ${RUNTIME_FILE}`, [bundlePath]);
  fs.rmSync(bundlePath, { force: true });
}
console.log(`${references.length} references from ${layout.managed}`);
process.exit(ok ? 0 : 1);
