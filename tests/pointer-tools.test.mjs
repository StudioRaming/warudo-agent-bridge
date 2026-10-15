import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { createServer } from '../src/mcp-server.mjs';
import { TOOLS } from '../src/tools.mjs';
import { startFakeRuntime } from './helpers/fake-runtime.mjs';

const byName = Object.fromEntries(TOOLS.map((tool) => [tool.name, tool]));
const call = (server, id, name, args = {}) => server.handle({ jsonrpc: '2.0', id, method: 'tools/call', params: { name, arguments: args } });
const payload = (reply) => JSON.parse(reply.result.content[0].text);

const POINTER_TOOLS = [
  'warudo_pointer_click', 'warudo_pointer_drag', 'warudo_pointer_move', 'warudo_pointer_button', 'warudo_pointer_wheel',
  'warudo_pointer_release', 'warudo_pointer_state', 'warudo_key_virtual', 'warudo_ui_list', 'warudo_ui_at', 'warudo_ui_click', 'warudo_ui_set_text',
];

test('pointer tools: reads need no lease, gestures and text entry take it, release is repeatable', () => {
  for (const name of POINTER_TOOLS) assert.ok(byName[name], `${name} exists`);
  for (const name of ['warudo_pointer_state', 'warudo_ui_list', 'warudo_ui_at']) {
    assert.equal(byName[name].kind, 'read', name);
    assert.equal(byName[name].needsLease, false, name);
  }
  for (const name of ['warudo_pointer_click', 'warudo_pointer_drag', 'warudo_pointer_move', 'warudo_pointer_button', 'warudo_pointer_wheel', 'warudo_key_virtual', 'warudo_ui_click', 'warudo_ui_set_text']) {
    assert.equal(byName[name].kind, 'write', name);
    assert.equal(byName[name].needsLease, true, name);
  }
  // Releasing twice is harmless, so a reload that interrupts it may be retried.
  assert.equal(byName.warudo_pointer_release.kind, 'set');
  assert.equal(byName.warudo_pointer_release.needsLease, true);
  assert.ok(POINTER_TOOLS.every((name) => byName[name].requires === 'pointer'), 'every pointer tool names the runtime feature it needs');
  assert.deepEqual(POINTER_TOOLS.filter((name) => byName[name].plan).sort(),
    ['warudo_key_virtual', 'warudo_pointer_button', 'warudo_pointer_click', 'warudo_pointer_drag', 'warudo_pointer_move', 'warudo_pointer_wheel']);
  assert.equal(byName.warudo_ui_click.local, 'uiClick');
  assert.ok(new Set(['pointer.play', 'pointer.release', 'pointer.state', 'ui.list', 'ui.at', 'ui.set_text']).has(byName.warudo_ui_click.operation));
});

test('pointer tool schemas ask for what the runtime needs', () => {
  assert.deepEqual(byName.warudo_pointer_click.inputSchema.required, ['x', 'y']);
  assert.deepEqual(byName.warudo_pointer_drag.inputSchema.required, ['path']);
  assert.equal(byName.warudo_pointer_drag.inputSchema.properties.path.maxItems, 64);
  assert.deepEqual(byName.warudo_pointer_wheel.inputSchema.required, ['x', 'y', 'notches']);
  assert.deepEqual(byName.warudo_ui_set_text.inputSchema.required, ['id', 'text']);
  assert.deepEqual(byName.warudo_key_virtual.inputSchema.required, ['key']);
  assert.deepEqual(byName.warudo_pointer_click.inputSchema.properties.space.enum, ['pixels', 'normalized']);
  assert.match(byName.warudo_pointer_click.description, /camera/, 'says what it does not reach');
  assert.match(byName.warudo_key_virtual.description, /warudo_input_key/, 'points hotkey users to the real hotkey tool');
});

async function pointerRuntime(handler, features = ['pointer']) {
  const runtime = await startFakeRuntime({
    handler: (op, params, owner) => {
      if (op === 'status') return { version: 'test', features };
      return handler(op, params, owner);
    },
  });
  return runtime;
}

test('a click is built in Node and played by pointer.play after a feature check', async () => {
  const runtime = await pointerRuntime((op) => {
    if (op === 'pointer.play') return { gesture: 'click', frames: 4, aborted: false };
    return {};
  });
  try {
    const server = createServer({ home: runtime.home, owner: 'agent-a', timeoutMs: 3000 });
    const reply = await call(server, 1, 'warudo_pointer_click', { x: 640, y: 360, button: 'right', modifiers: ['alt'], expectObject: 77, settleFrames: 3 });
    assert.equal(reply.result.isError, false);
    assert.equal(payload(reply).gesture, 'click');
    const play = runtime.seen.find((request) => request.operation === 'pointer.play');
    assert.deepEqual(runtime.seen.map((request) => request.operation).slice(0, 2), ['status', 'pointer.play']);
    assert.equal(play.params.frames, 4);
    assert.deepEqual(play.params.steps, [{ f: 0, x: 640, y: 360 }, { f: 1, set: 2 }, { f: 3, clear: 2 }]);
    assert.deepEqual(play.params.holdKeys, ['LeftAlt']);
    assert.equal(play.params.expectObject, 77);
    assert.equal(play.params.settleFrames, 3);
    assert.equal(play.params.gesture, 'click');
  } finally {
    await runtime.stop();
  }
});

test('a bad gesture is refused in Node and never reaches WARUDO', async () => {
  const runtime = await pointerRuntime(() => ({}));
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const reply = await call(server, 1, 'warudo_pointer_drag', { path: Array.from({ length: 11 }, (_, index) => [index, 0]), frames: 60 });
    assert.equal(reply.result.isError, true);
    assert.equal(payload(reply).code, 'GESTURE_TOO_LONG');
    const wheel = await call(server, 2, 'warudo_pointer_wheel', { x: 1, y: 1, notches: 0 });
    assert.equal(payload(wheel).code, 'INVALID_ARGUMENT');
    assert.ok(runtime.seen.every((request) => request.operation !== 'pointer.play'));
  } finally {
    await runtime.stop();
  }
});

test('an installed runtime without the pointer feature answers RUNTIME_OUTDATED and nothing else runs', async () => {
  const runtime = await pointerRuntime(() => ({}), ['claim', 'plugin-settings']);
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    for (const [name, args] of [['warudo_pointer_click', { x: 1, y: 1 }], ['warudo_ui_list', {}], ['warudo_ui_click', { label: 'Apply' }], ['warudo_pointer_release', {}]]) {
      const reply = await call(server, 1, name, args);
      assert.equal(reply.result.isError, true, name);
      const error = payload(reply);
      assert.equal(error.code, 'RUNTIME_OUTDATED', name);
      assert.match(error.message, /Reinstall/);
      assert.match(error.message, /"pointer"/);
    }
    assert.ok(runtime.seen.every((request) => request.operation === 'status'), 'only the feature check ran');
    const status = await call(server, 2, 'warudo_status');
    assert.equal(status.result.isError, false, 'tools without the requirement still work');
  } finally {
    await runtime.stop();
  }
});

const SCREEN = { width: 1920, height: 1080 };
const row = (id, label, over = {}) => ({ id, path: `Canvas/${label}`, label, rect: [100, 200, 40, 20], interactable: true, onScreen: true, handlers: ['Button'], ...over });

test('ui_click resolves the label with ui.list and clicks the center with expectObject set', async () => {
  const listings = [];
  const runtime = await pointerRuntime((op, params) => {
    if (op === 'ui.list') { listings.push(params); return { screen: SCREEN, total: 2, truncated: false, items: [row(11, 'Apply'), row(12, 'Cancel', { rect: [300, 400, 60, 30] })] }; }
    if (op === 'pointer.play') return { gesture: 'click', press: { hit: { top: { objectId: 12 } } } };
    return {};
  });
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const reply = await call(server, 1, 'warudo_ui_click', { label: 'cancel', labelMatch: 'exact', modifiers: ['shift'] });
    assert.equal(reply.result.isError, false, reply.result.content[0].text);
    const data = payload(reply);
    assert.deepEqual(data.resolved, { id: 12, path: 'Canvas/Cancel', label: 'Cancel', point: [330, 415] });
    assert.deepEqual(listings, [{ query: 'cancel', interactableOnly: false, limit: 500 }]);
    const play = runtime.seen.find((request) => request.operation === 'pointer.play');
    assert.deepEqual(play.params.steps[0], { f: 0, x: 330, y: 415 });
    assert.equal(play.params.expectObject, 12, 'a covered control fails with COVERED instead of clicking what is on top');
    assert.deepEqual(play.params.holdKeys, ['LeftShift']);
    // An id is looked up by id and skips the label rules.
    await call(server, 2, 'warudo_ui_click', { id: 11 });
    assert.deepEqual(listings[1], { id: 11, interactableOnly: false, limit: 500 });
  } finally {
    await runtime.stop();
  }
});

test('ui_click: not found, ambiguous, disabled and a bad selector stop before any click', async () => {
  const runtime = await pointerRuntime((op) => {
    if (op === 'ui.list') {
      return { screen: SCREEN, total: 3, truncated: false, items: [row(1, 'Apply'), row(2, 'Apply all'), row(3, 'Save', { interactable: false })] };
    }
    return {};
  });
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const code = async (args) => payload(await call(server, 1, 'warudo_ui_click', args)).code;
    assert.equal(await code({ label: 'nothing like this' }), 'UI_NOT_FOUND');
    assert.equal(await code({ label: 'apply' }), 'UI_AMBIGUOUS');
    assert.equal(await code({ label: 'save' }), 'UI_NOT_CLICKABLE');
    assert.equal(await code({}), 'INVALID_ARGUMENT');
    assert.equal(await code({ id: 1, label: 'Apply' }), 'INVALID_ARGUMENT');
    const ambiguous = payload(await call(server, 2, 'warudo_ui_click', { label: 'apply' }));
    assert.deepEqual(ambiguous.details.candidates.map((candidate) => candidate.id), [1, 2]);
    assert.ok(runtime.seen.every((request) => request.operation !== 'pointer.play'));
    // index settles the ambiguity.
    const picked = await call(server, 3, 'warudo_ui_click', { label: 'apply', index: 1 });
    assert.equal(picked.result.isError, false);
    assert.equal(runtime.seen.find((request) => request.operation === 'pointer.play').params.expectObject, 2);
  } finally {
    await runtime.stop();
  }
});

test('a gesture with capture=true comes back with the screenshot as an image', async () => {
  const runtime = await pointerRuntime(async (op) => {
    if (op !== 'pointer.play') return {};
    const file = path.join(runtime.root, 'evidence', 'click.png');
    await fs.writeFile(file, Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
    return { gesture: 'click', capture: { path: file, width: 1, height: 1 } };
  });
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const reply = await call(server, 1, 'warudo_pointer_click', { x: 5, y: 5, capture: true });
    assert.equal(reply.result.isError, false);
    assert.equal(reply.result.content[1].type, 'image');
    assert.equal(reply.result.content[1].mimeType, 'image/png');
  } finally {
    await runtime.stop();
  }
});

test('ui reads and text entry pass their arguments through unchanged', async () => {
  const runtime = await pointerRuntime((op, params) => ({ echo: op, params }));
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const listed = payload(await call(server, 1, 'warudo_ui_list', { query: 'Mask', interactableOnly: false, limit: 20 }));
    assert.deepEqual(listed, { echo: 'ui.list', params: { query: 'Mask', interactableOnly: false, limit: 20 } });
    const at = payload(await call(server, 2, 'warudo_ui_at', { x: 0.5, y: 0.5, space: 'normalized' }));
    assert.deepEqual(at.params, { x: 0.5, y: 0.5, space: 'normalized' });
    const typed = payload(await call(server, 3, 'warudo_ui_set_text', { id: 9, text: 'hello', submit: true }));
    assert.deepEqual(typed, { echo: 'ui.set_text', params: { id: 9, text: 'hello', submit: true } });
    const state = payload(await call(server, 4, 'warudo_pointer_state'));
    assert.equal(state.echo, 'pointer.state');
    const released = payload(await call(server, 5, 'warudo_pointer_release'));
    assert.equal(released.echo, 'pointer.release');
  } finally {
    await runtime.stop();
  }
});

test('the feature check runs once per runtime session, not once per call', async () => {
  const runtime = await pointerRuntime(() => ({ ok: 1 }));
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const statusCalls = () => runtime.seen.filter((request) => request.operation === 'status').length;
    for (let id = 1; id <= 4; id += 1) assert.equal((await call(server, id, 'warudo_ui_list')).result.isError, false);
    assert.equal(statusCalls(), 1, 'four calls, one feature check');
    // A reload gives the runtime a new sessionId: the next call checks again, then stays quiet.
    await runtime.writeState({ sessionId: 'b'.repeat(32) });
    for (let id = 5; id <= 7; id += 1) assert.equal((await call(server, id, 'warudo_pointer_state')).result.isError, false);
    assert.equal(statusCalls(), 2);
  } finally {
    await runtime.stop();
  }
});

test('a missing feature is never cached, so a reinstall is noticed on the next call', async () => {
  let features = ['claim'];
  const runtime = await startFakeRuntime({ handler: (op) => (op === 'status' ? { version: 'test', features } : { ok: 1 }) });
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    assert.equal(payload(await call(server, 1, 'warudo_ui_list')).code, 'RUNTIME_OUTDATED');
    features = ['claim', 'pointer'];
    assert.equal((await call(server, 2, 'warudo_ui_list')).result.isError, false);
  } finally {
    await runtime.stop();
  }
});
