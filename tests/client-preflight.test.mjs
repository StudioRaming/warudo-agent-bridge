import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import childProcess from 'node:child_process';
import { syncBuiltinESMExports } from 'node:module';
import { install, uninstall } from '../src/install.mjs';

for (const [name, operation] of [['install', install], ['uninstall', uninstall]]) {
  for (const clients of [undefined, '', 'auto', 'none', 'claude-code', 'codex',
    'claude-desktop', 'cursor', ' codex, cursor ']) {
    test(name + ' accepts client selection ' + JSON.stringify(clients), (t) => {
      // Stop at the first I/O boundary: valid input must get past preflight,
      // but these tests must never install, remove, discover or register anything.
      const expected = new Error('Stopped before external effects');
      let touched = 0;
      const stop = () => { touched++; throw expected; };
      for (const method of ['existsSync', 'readFileSync', 'readdirSync', 'mkdirSync',
        'cpSync', 'rmSync', 'renameSync', 'writeFileSync', 'copyFileSync']) {
        t.mock.method(fs, method, stop);
      }
      for (const method of ['spawnSync', 'execFileSync']) {
        t.mock.method(childProcess, method, stop);
      }
      syncBuiltinESMExports();
      try {
        assert.throws(() => operation({
          home: '/isolated-wab-preflight-test',
          warudo: '/isolated-warudo-preflight-test',
          clients,
        }), (error) => error === expected);
        assert.equal(touched, 1);
      } finally {
        t.mock.restoreAll();
        syncBuiltinESMExports();
      }
    });
  }
}

// No actual installation, client discovery, runtime change or filesystem access.
// Tripwires stop the operation at its first external effect on the unfixed code.
for (const [name, operation] of [['install', install], ['uninstall', uninstall]]) {
  for (const clients of ['not-a-client', 'codex,not-a-client']) {
    test(name + ' rejects ' + clients + ' before any external effect', (t) => {
      const effects = [];
      const forbid = (label) => () => {
        effects.push(label);
        throw new Error('Unexpected external effect: ' + label);
      };
      for (const method of ['existsSync', 'readFileSync', 'readdirSync', 'mkdirSync',
        'cpSync', 'rmSync', 'renameSync', 'writeFileSync', 'copyFileSync']) {
        t.mock.method(fs, method, forbid('fs.' + method));
      }
      for (const method of ['spawnSync', 'execFileSync']) {
        t.mock.method(childProcess, method, forbid('child_process.' + method));
      }
      syncBuiltinESMExports();
      try {
        assert.throws(() => operation({
          home: '/isolated-wab-preflight-test',
          warudo: '/isolated-warudo-preflight-test',
          clients,
        }), /Unknown client\(s\): not-a-client/);
        assert.deepEqual(effects, []);
      } finally {
        t.mock.restoreAll();
        syncBuiltinESMExports();
      }
    });
  }
}
