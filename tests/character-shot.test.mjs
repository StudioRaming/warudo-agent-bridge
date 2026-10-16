import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import { createServer, INSTRUCTIONS } from '../src/mcp-server.mjs';
import { TOOLS } from '../src/tools.mjs';
import { startFakeRuntime } from './helpers/fake-runtime.mjs';

const shot = TOOLS.find((tool) => tool.name === 'warudo_character_shot');
const call = (server, id, name, args = {}) => server.handle({ jsonrpc: '2.0', id, method: 'tools/call', params: { name, arguments: args } });
const payload = (reply) => JSON.parse(reply.result.content[0].text);

test('warudo_character_shot is a lease-free read that needs the character-shot feature', () => {
  assert.equal(shot.operation, 'character.shot');
  assert.equal(shot.kind, 'read');
  assert.equal(shot.needsLease, false);
  assert.equal(shot.requires, 'character-shot');
  assert.deepEqual(shot.inputSchema.required, []);
  assert.equal(shot.inputSchema.properties.size.maximum, 2048);
  assert.equal(shot.inputSchema.properties.views.maxItems, 6);
});

test('agents are told to use it for pictures of the character', () => {
  assert.match(INSTRUCTIONS, /warudo_character_shot/);
  assert.match(INSTRUCTIONS, /Character asset in the asset list means there is a character/);
  for (const name of ['warudo_camera_capture', 'warudo_screen_capture']) {
    assert.match(TOOLS.find((tool) => tool.name === name).description, /For a picture of the character, use warudo_character_shot\./, name);
  }
  assert.match(fs.readFileSync(new URL('../README.md', import.meta.url), 'utf8'), /warudo_character_shot/);
});

test('option validation answers before anything is queued', async () => {
  const runtime = await startFakeRuntime({ handler: (op) => (op === 'status' ? { version: 'test', features: ['character-shot'] } : {}) });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    const bad = [
      { framing: 'torso' }, { background: 'green' }, { size: 100 }, { size: 4096 }, { size: 800.5 }, { pitch: 95 }, { fov: 5 }, { fit: 1.2 },
      { views: [] }, { views: ['front', 'upside-down'] }, { views: ['front', 'front'] }, { views: ['a', 'b', 'c', 'd', 'e', 'f', 'g'] },
      { views: ['front'], yaw: 30 }, { yaw: 'left' }, { yaw: Infinity },
    ];
    for (const args of bad) {
      const reply = await call(server, 1, 'warudo_character_shot', args);
      assert.equal(reply.result.isError, true, JSON.stringify(args));
      assert.equal(payload(reply).code, 'INVALID_ARGUMENT', JSON.stringify(args));
    }
    assert.deepEqual(runtime.seen, [], 'nothing reached the runtime');
  } finally {
    await runtime.stop();
  }
});

test('a valid shot is forwarded as character.shot without a lease', async () => {
  const runtime = await startFakeRuntime({
    handler: (op, params) => {
      if (op === 'status') return { version: 'test', features: ['character-shot'] };
      return { subject: { name: 'MAYO' }, params };
    },
  });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    const args = { framing: 'face', views: ['front', 'three-quarter', 'top'], pitch: 0, fov: 30, fit: 0.9, background: 'transparent', size: 2048 };
    const reply = await call(server, 1, 'warudo_character_shot', args);
    assert.equal(reply.result.isError, false);
    assert.deepEqual(runtime.seen.map((request) => request.operation), ['status', 'character.shot']);
    assert.deepEqual(runtime.seen[1].params, args);
  } finally {
    await runtime.stop();
  }
});

test('an older runtime without the feature is reported, not called', async () => {
  const runtime = await startFakeRuntime({ handler: (op) => (op === 'status' ? { version: 'test', features: ['claim'] } : {}) });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    const reply = await call(server, 1, 'warudo_character_shot', {});
    assert.equal(reply.result.isError, true);
    assert.equal(payload(reply).code, 'RUNTIME_OUTDATED');
    assert.equal(runtime.seen.some((request) => request.operation === 'character.shot'), false);
  } finally {
    await runtime.stop();
  }
});
