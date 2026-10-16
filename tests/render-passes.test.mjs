import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import { createServer } from '../src/mcp-server.mjs';
import { RENDER_PASSES, TOOLS } from '../src/tools.mjs';
import { startFakeRuntime } from './helpers/fake-runtime.mjs';

const pass = TOOLS.find((tool) => tool.name === 'warudo_render_pass');
const isolate = TOOLS.find((tool) => tool.name === 'warudo_render_isolate');
const call = (server, id, name, args = {}) => server.handle({ jsonrpc: '2.0', id, method: 'tools/call', params: { name, arguments: args } });
const payload = (reply) => JSON.parse(reply.result.content[0].text);
const status = (op) => (op === 'status' ? { version: 'test', features: ['render-passes'] } : null);

test('both render tools are lease-free reads that need the render-passes feature', () => {
  for (const [tool, operation] of [[pass, 'render.pass'], [isolate, 'render.isolate']]) {
    assert.equal(tool.operation, operation);
    assert.equal(tool.kind, 'read');
    assert.equal(tool.needsLease, false);
    assert.equal(tool.requires, 'render-passes');
  }
  assert.deepEqual(pass.inputSchema.required, ['passes']);
  assert.deepEqual(pass.inputSchema.properties.passes.items.enum, RENDER_PASSES);
  assert.equal(pass.inputSchema.properties.size.maximum, 1024);
  assert.equal(pass.inputSchema.properties.maxSize.default, 1600);
  assert.equal(isolate.inputSchema.properties.maxSize.default, 1600);
  assert.equal(isolate.inputSchema.properties.objectIds.maxItems, 16);
});

test('the feature id is advertised by the runtime source and both operations are lease-free', () => {
  const source = fs.readFileSync(new URL('../runtime/AgentBridgePlugin.cs', import.meta.url), 'utf8');
  assert.match(source, /"render-passes"/);
  assert.match(source, /"render\.pass", "render\.isolate"/);
  assert.match(fs.readFileSync(new URL('../README.md', import.meta.url), 'utf8'), /warudo_render_pass/);
  assert.match(fs.readFileSync(new URL('../README.md', import.meta.url), 'utf8'), /warudo_render_isolate/);
});

test('pass options are checked before anything is queued', async () => {
  const runtime = await startFakeRuntime({ handler: (op) => status(op) || {} });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    const bad = [
      {}, { passes: [] }, { passes: ['shiny'] }, { passes: ['parts', 'parts'] }, { passes: 'parts' },
      { passes: ['texture'] }, { passes: ['texture'], textureProperty: 'bad name' }, { passes: ['parts'], textureProperty: '' },
      { passes: ['parts'], views: [] }, { passes: ['parts'], views: ['camera', 'sideways'] }, { passes: ['parts'], views: ['front', 'front'] },
      { passes: ['parts', 'albedo', 'normals', 'depth', 'uv'], views: ['camera', 'front', 'back'] },
      { passes: ['parts'], size: 100 }, { passes: ['parts'], size: 2048 }, { passes: ['parts'], size: 600.5 }, { passes: ['parts'], maxSize: 100 },
      { passes: ['parts'], labels: 'yes' }, { passes: ['parts'], include: 'hair' }, { passes: ['parts'], exclude: [1] },
    ];
    for (const args of bad) {
      const reply = await call(server, 1, 'warudo_render_pass', args);
      assert.equal(reply.result.isError, true, JSON.stringify(args));
      assert.equal(payload(reply).code, 'INVALID_ARGUMENT', JSON.stringify(args));
    }
    assert.deepEqual(runtime.seen, [], 'nothing reached the runtime');
  } finally {
    await runtime.stop();
  }
});

test('isolate options are checked before anything is queued', async () => {
  const runtime = await startFakeRuntime({ handler: (op) => status(op) || {} });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    const bad = [
      {}, { objectIds: [] }, { objectIds: ['a'] }, { objectIds: [1.5] }, { objectIds: [1, 1] }, { include: [] }, { include: [''] }, { include: 'ribbon' },
      { include: ['a'], mode: 'solo' }, { include: ["a"], mode: "only", background: "green" }, { include: ['a'], background: 'checker' },
      { include: ['a'], size: 100 }, { include: ['a'], size: 4096 }, { include: ['a'], maxSize: 100 },
      { objectIds: Array.from({ length: 17 }, (_, index) => index) },
    ];
    for (const args of bad) {
      const reply = await call(server, 1, 'warudo_render_isolate', args);
      assert.equal(reply.result.isError, true, JSON.stringify(args));
      assert.equal(payload(reply).code, 'INVALID_ARGUMENT', JSON.stringify(args));
    }
    assert.deepEqual(runtime.seen, [], 'nothing reached the runtime');
  } finally {
    await runtime.stop();
  }
});

test('valid calls are forwarded unchanged and without a lease', async () => {
  const runtime = await startFakeRuntime({ handler: (op, params) => status(op) || { params } });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    const passArgs = { passes: ['parts', 'albedo', 'texture', 'alpha'], views: ['camera', 'front'], textureProperty: '_ShadeTex', include: ['Hair'], labels: false, size: 512, maxSize: 1200 };
    const isolateArgs = { include: ['Ribbon'], objectIds: [12, 13], mode: 'only', background: 'checker', size: 1024 };
    assert.equal((await call(server, 1, 'warudo_render_pass', passArgs)).result.isError, false);
    assert.equal((await call(server, 2, 'warudo_render_isolate', isolateArgs)).result.isError, false);
    assert.deepEqual(runtime.seen.map((request) => request.operation).filter((op) => op !== 'status'), ['render.pass', 'render.isolate']);
    assert.deepEqual(runtime.seen.find((request) => request.operation === 'render.pass').params, passArgs);
    assert.deepEqual(runtime.seen.find((request) => request.operation === 'render.isolate').params, isolateArgs);
    assert.equal(runtime.seen.some((request) => /lease/.test(request.operation)), false);
  } finally {
    await runtime.stop();
  }
});

test('an older runtime without the feature is reported, not called', async () => {
  const runtime = await startFakeRuntime({ handler: (op) => (op === 'status' ? { version: 'test', features: ['claim'] } : {}) });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    for (const [name, args] of [['warudo_render_pass', { passes: ['parts'] }], ['warudo_render_isolate', { include: ['x'] }]]) {
      const reply = await call(server, 1, name, args);
      assert.equal(reply.result.isError, true, name);
      assert.equal(payload(reply).code, 'RUNTIME_OUTDATED', name);
    }
    assert.equal(runtime.seen.some((request) => request.operation.startsWith('render.')), false);
  } finally {
    await runtime.stop();
  }
});
