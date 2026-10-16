import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { createServer } from '../src/mcp-server.mjs';
import { TOOLS } from '../src/tools.mjs';
import { startFakeRuntime } from './helpers/fake-runtime.mjs';

const tool = TOOLS.find((entry) => entry.name === 'warudo_ui_language');
const call = (server, id, name, args = {}) => server.handle({ jsonrpc: '2.0', id, method: 'tools/call', params: { name, arguments: args } });
const payload = (reply) => JSON.parse(reply.result.content[0].text);

test('warudo_ui_language is a lease-taking set tool that needs the ui-language feature', () => {
  assert.ok(tool);
  assert.equal(tool.kind, 'set');
  assert.equal(tool.needsLease, true);
  assert.equal(tool.operation, 'ui.language');
  assert.equal(tool.requires, 'ui-language');
  assert.deepEqual(tool.inputSchema.required, []);
  assert.equal(tool.inputSchema.additionalProperties, false);
  assert.match(tool.description, /Nothing is saved to disk/, 'says it does not persist');
  assert.match(tool.description, /warudo_pointer_release/, 'says when it is restored');
});

test('the arguments reach the runtime unchanged, and an older runtime is refused before anything is sent', async () => {
  const features = { list: ['ui-language'] };
  const runtime = await startFakeRuntime({
    handler: (op, params) => (op === 'status' ? { version: 'test', features: features.list } : { echo: op, params }),
  });
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    assert.deepEqual(payload(await call(server, 1, 'warudo_ui_language', { code: 'ja' })), { echo: 'ui.language', params: { code: 'ja' } });
    assert.deepEqual(payload(await call(server, 2, 'warudo_ui_language', { restore: true })), { echo: 'ui.language', params: { restore: true } });
  } finally {
    await runtime.stop();
  }
  features.list = ['pointer'];
  const old = await startFakeRuntime({ handler: (op) => (op === 'status' ? { version: 'test', features: features.list } : { echo: op }) });
  try {
    const server = createServer({ home: old.home, timeoutMs: 3000 });
    const reply = await call(server, 1, 'warudo_ui_language', { code: 'ja' });
    assert.equal(reply.result.isError, true);
    assert.match(reply.result.content[0].text, /RUNTIME_OUTDATED/);
    assert.equal(old.seen.filter((request) => request.operation === 'ui.language').length, 0);
  } finally {
    await old.stop();
  }
});

test('restore bookkeeping: first change remembers the original, later ones keep it, restore clears it', (context) => {
  // runtime/LanguageRestore.cs runs under plain .NET (scripts/language-restore/Program.cs); exit code 2 means there is no .NET SDK here.
  const run = spawnSync(process.execPath, ['scripts/check-language-restore.mjs'], { cwd: new URL('..', import.meta.url), encoding: 'utf8' });
  if (run.status === 2) return context.skip(run.stderr.trim());
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, /ALL OK/);
});
