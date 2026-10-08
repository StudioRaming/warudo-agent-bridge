import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { createServer, LEGACY_VERSIONS, MODERN_VERSIONS } from '../src/mcp-server.mjs';
import { TOOLS } from '../src/tools.mjs';
import { startFakeRuntime } from './helpers/fake-runtime.mjs';

const META = (version = MODERN_VERSIONS[0]) => ({ _meta: { 'io.modelcontextprotocol/protocolVersion': version } });
const call = (server, id, method, params) => server.handle({ jsonrpc: '2.0', id, method, params });

test('legacy initialize echoes a supported version and falls back to the newest legacy one', async () => {
  const server = createServer({ home: 'unused' });
  const known = await call(server, 1, 'initialize', { protocolVersion: '2025-06-18', capabilities: {} });
  assert.equal(known.result.protocolVersion, '2025-06-18');
  assert.equal(known.result.serverInfo.name, 'warudo-agent-bridge');
  const unknown = await call(server, 2, 'initialize', { protocolVersion: '1999-01-01' });
  assert.equal(unknown.result.protocolVersion, LEGACY_VERSIONS[0]);
  assert.equal(known.result.resultType, undefined, 'legacy results carry no resultType');
});

test('modern requests get resultType, discovery, and a -32022 error for unsupported versions', async () => {
  const server = createServer({ home: 'unused' });
  const discover = await call(server, 1, 'server/discover', META());
  assert.equal(discover.result.resultType, 'complete');
  assert.deepEqual(discover.result.supportedVersions, [...MODERN_VERSIONS, ...LEGACY_VERSIONS]);
  assert.equal(discover.result._meta['io.modelcontextprotocol/serverInfo'].name, 'warudo-agent-bridge');
  const bad = await call(server, 2, 'tools/list', META('1900-01-01'));
  assert.equal(bad.error.code, -32022);
  assert.equal(bad.error.data.requested, '1900-01-01');
  assert.ok(bad.error.data.supported.includes(MODERN_VERSIONS[0]));
});

test('notifications never get a reply and unknown methods and tools are protocol errors', async () => {
  const server = createServer({ home: 'unused' });
  assert.equal(await server.handle({ jsonrpc: '2.0', method: 'notifications/initialized' }), null);
  assert.equal(await server.handle({ jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: 3 } }), null);
  assert.equal((await call(server, 1, 'no/such/method', {})).error.code, -32601);
  assert.equal((await call(server, 2, 'tools/call', { name: 'warudo_nope', arguments: {} })).error.code, -32602);
});

test('modern list results carry ttlMs and cacheScope; legacy list results do not', async () => {
  const server = createServer({ home: 'unused' });
  const lists = [
    ['tools/list', 'tools'],
    ['prompts/list', 'prompts'],
    ['resources/list', 'resources'],
    ['resources/templates/list', 'resourceTemplates'],
  ];
  for (const [method, key] of lists) {
    const modern = (await call(server, 1, method, META())).result;
    assert.equal(modern.resultType, 'complete', `${method} modern resultType`);
    assert.equal(typeof modern.ttlMs, 'number', `${method} needs a number ttlMs`);
    assert.ok(modern.ttlMs >= 0, `${method} ttlMs must be >= 0`);
    assert.ok(['public', 'private'].includes(modern.cacheScope), `${method} cacheScope must be public or private`);
    assert.ok(Array.isArray(modern[key]), `${method} keeps its ${key} array`);
    const legacy = (await call(server, 2, method, {})).result;
    assert.equal('ttlMs' in legacy, false, `${method} legacy reply must not add ttlMs`);
    assert.equal('cacheScope' in legacy, false, `${method} legacy reply must not add cacheScope`);
  }
});

test('every tool is well formed for strict MCP clients', async () => {
  const server = createServer({ home: 'unused' });
  const { result } = await call(server, 1, 'tools/list', {});
  const names = new Set();
  for (const tool of result.tools) {
    assert.match(tool.name, /^warudo_[a-z0-9_]+$/);
    assert.ok(!names.has(tool.name), `duplicate tool ${tool.name}`);
    names.add(tool.name);
    assert.ok(tool.title && tool.description.length > 20, `${tool.name} needs a title and description`);
    assert.equal(tool.inputSchema.type, 'object');
    assert.equal(tool.inputSchema.additionalProperties, false);
    for (const key of tool.inputSchema.required) assert.ok(key in tool.inputSchema.properties, `${tool.name} requires unknown ${key}`);
    assert.equal(typeof tool.annotations.readOnlyHint, 'boolean');
  }
  assert.equal(result.tools.length, TOOLS.length);
});

test('a write takes the free lease automatically and retries once', async () => {
  let leaseOwner = null;
  const runtime = await startFakeRuntime({
    handler: (op, params, owner) => {
      if (op === 'lease.acquire') { leaseOwner = owner; return { leaseOwner }; }
      if (op === 'camera.zoom') {
        if (leaseOwner !== owner) throw new Error('LEASE_REQUIRED: acquire the write lease first.');
        return { after: params.fieldOfView };
      }
      return {};
    },
  });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    const reply = await call(server, 1, 'tools/call', { name: 'warudo_camera_zoom', arguments: { fieldOfView: 35 } });
    assert.equal(reply.result.isError, false);
    assert.deepEqual(JSON.parse(reply.result.content[0].text), { after: 35 });
    assert.deepEqual(runtime.seen.map((r) => r.operation), ['camera.zoom', 'lease.acquire', 'camera.zoom']);
  } finally {
    await runtime.stop();
  }
});

test('a lease held by another agent is surfaced, not overridden', async () => {
  const runtime = await startFakeRuntime({
    handler: (op) => {
      if (op === 'lease.acquire') throw new Error('LEASE_BUSY: held by agent-b');
      throw new Error('LEASE_REQUIRED: acquire the write lease first.');
    },
  });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    const reply = await call(server, 1, 'tools/call', { name: 'warudo_asset_enabled', arguments: { assetId: 'x', enabled: false } });
    assert.equal(reply.result.isError, true);
    assert.equal(JSON.parse(reply.result.content[0].text).code, 'LEASE_BUSY');
    const noAuto = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000, autoLease: false });
    const manual = await call(noAuto, 2, 'tools/call', { name: 'warudo_asset_enabled', arguments: { assetId: 'x', enabled: false } });
    assert.equal(JSON.parse(manual.result.content[0].text).code, 'LEASE_REQUIRED');
  } finally {
    await runtime.stop();
  }
});

test('captures come back as image content and structured content follows the negotiated version', async () => {
  const runtime = await startFakeRuntime({
    handler: async (op) => {
      if (op !== 'camera.capture') return { ok: 1 };
      const file = path.join(runtime.root, 'evidence', 'cap.png');
      await fs.writeFile(file, Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
      return { path: file, width: 1, height: 1 };
    },
  });
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    await call(server, 0, 'initialize', { protocolVersion: '2024-11-05' });
    const old = await call(server, 1, 'tools/call', { name: 'warudo_camera_capture', arguments: {} });
    assert.equal(old.result.content[1].type, 'image');
    assert.equal(old.result.content[1].mimeType, 'image/png');
    assert.equal(old.result.structuredContent, undefined);
    await call(server, 2, 'initialize', { protocolVersion: '2025-06-18' });
    const newer = await call(server, 3, 'tools/call', { name: 'warudo_status', arguments: {} });
    assert.deepEqual(newer.result.structuredContent, { ok: 1 });
    const modern = await call(server, 4, 'tools/call', { name: 'warudo_status', arguments: {}, ...META() });
    assert.equal(modern.result.resultType, 'complete');
    assert.deepEqual(modern.result.structuredContent, { ok: 1 });
  } finally {
    await runtime.stop();
  }
});

test('asset_wait_loaded polls until the asset is active with a GameObject', async () => {
  let polls = 0;
  const runtime = await startFakeRuntime({
    handler: (op) => {
      polls += 1;
      return { id: 'a', active: polls >= 3, gameObjectId: polls >= 3 ? 42 : null, source: 'character://data/Characters/x.vrm' };
    },
  });
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const reply = await call(server, 1, 'tools/call', { name: 'warudo_asset_wait_loaded', arguments: { assetId: 'a', source: 'character://data/Characters/x.vrm', timeoutMs: 5000 } });
    const data = JSON.parse(reply.result.content[0].text);
    assert.equal(data.loaded, true);
    assert.ok(runtime.seen.every((r) => r.operation === 'asset.get'));
  } finally {
    await runtime.stop();
  }
});

test('the stdio entry point speaks JSON-RPC lines', async () => {
  const runtime = await startFakeRuntime();
  const bin = fileURLToPath(new URL('../bin/warudo-agent-bridge.mjs', import.meta.url));
  const child = spawn(process.execPath, [bin, 'mcp', '--home', runtime.home], { stdio: ['pipe', 'pipe', 'inherit'] });
  try {
    const lines = [];
    child.stdout.setEncoding('utf8');
    let buffer = '';
    child.stdout.on('data', (chunk) => {
      buffer += chunk;
      let index;
      while ((index = buffer.indexOf('\n')) >= 0) { lines.push(JSON.parse(buffer.slice(0, index))); buffer = buffer.slice(index + 1); }
    });
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-11-25' } })}\n`);
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' })}\n`);
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'warudo_status', arguments: {} } })}\n`);
    const deadline = Date.now() + 5000;
    while (lines.length < 2 && Date.now() < deadline) await new Promise((resolve) => setTimeout(resolve, 20));
    assert.equal(lines.length, 2, 'the notification must not produce output');
    assert.equal(lines[0].result.protocolVersion, '2025-11-25');
    assert.equal(lines[1].result.isError, false);
    const exited = new Promise((resolve) => child.once('exit', resolve));
    child.stdin.end();
    assert.equal(await exited, 0);
  } finally {
    child.kill();
    await runtime.stop();
  }
});
