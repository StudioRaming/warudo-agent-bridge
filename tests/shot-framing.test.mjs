import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

// The framing math of warudo_character_shot is pure C# (runtime/AgentBridgePlugin.ShotMath.cs). This test builds it on its
// own with the .NET SDK that scripts/compile-runtime.mjs needs anyway, and skips when there is none.
const project = path.join(path.dirname(fileURLToPath(import.meta.url)), 'csharp', 'ShotMathCheck', 'ShotMathCheck.csproj');
const version = spawnSync('dotnet', ['--version'], { encoding: 'utf8' });
const major = version.status === 0 ? Number.parseInt(version.stdout, 10) : 0;

test('upper framing aims at the center of its box and leaves even margins (pure framing math)', { skip: major >= 8 ? false : 'no .NET SDK 8 or newer' }, () => {
  const artifacts = fs.mkdtempSync(path.join(os.tmpdir(), 'shotmathcheck-'));
  try {
    const run = spawnSync('dotnet', ['run', '-c', 'Release', '--project', project, `-p:ShotTfm=net${major}.0`, '--artifacts-path', artifacts], { encoding: 'utf8', timeout: 180000 });
    const lines = `${run.stdout}${run.stderr}`.split(/\r?\n/).filter((line) => /^(PASS|FAIL) /.test(line));
    assert.ok(lines.length >= 8, `the harness printed its checks: ${run.stdout}${run.stderr}`);
    assert.deepEqual(lines.filter((line) => line.startsWith('FAIL')), []);
    assert.equal(run.status, 0);
  } finally {
    fs.rmSync(artifacts, { recursive: true, force: true });
  }
});
