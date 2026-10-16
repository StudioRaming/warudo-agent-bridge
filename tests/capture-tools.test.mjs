import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { captureOptionsUsed, normalizeCaptureOptions, validateCaptureArgs, validateRegion } from '../src/capture-options.mjs';
import { createServer } from '../src/mcp-server.mjs';
import { buildClickPlan } from '../src/pointer-timeline.mjs';
import { TOOLS } from '../src/tools.mjs';
import { startFakeRuntime } from './helpers/fake-runtime.mjs';

const byName = Object.fromEntries(TOOLS.map((tool) => [tool.name, tool]));
const call = (server, id, name, args = {}) => server.handle({ jsonrpc: '2.0', id, method: 'tools/call', params: { name, arguments: args } });
const payload = (reply) => JSON.parse(reply.result.content[0].text);
const SCREEN = { width: 2560, height: 1440 };

test('capture options: limits, exclusivity and the grid switch', () => {
  assert.deepEqual(normalizeCaptureOptions({}), {});
  assert.deepEqual(normalizeCaptureOptions({ maxSize: 256, gridStep: 50, padding: 0 }), { maxSize: 256, gridStep: 50, padding: 0, grid: true }, 'a gridStep turns the grid on');
  assert.deepEqual(normalizeCaptureOptions({ maxSize: 4096, grid: false, gridStep: 10 }).grid, false);
  for (const bad of [{ maxSize: 255 }, { maxSize: 4097 }, { maxSize: 1600.5 }, { maxSize: '1600' }, { gridStep: 9 }, { gridStep: 1001 }, { padding: -1 }, { padding: 401 }, { grid: 'yes' }, { markPointer: 1 }]) {
    assert.throws(() => normalizeCaptureOptions(bad), { code: 'INVALID_ARGUMENT' }, JSON.stringify(bad));
  }
  assert.throws(() => normalizeCaptureOptions({ region: [0, 0, 10, 10], around: { path: 'A' } }), { code: 'INVALID_ARGUMENT', message: /not both/ });
  assert.throws(() => normalizeCaptureOptions({ around: {} }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => normalizeCaptureOptions({ around: { objectId: 1, path: 'A' } }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => normalizeCaptureOptions({ around: { objectId: 1.5 } }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => normalizeCaptureOptions({ around: { path: '' } }), { code: 'INVALID_ARGUMENT' });
  assert.deepEqual(normalizeCaptureOptions({ around: { objectId: 12 } }).around, { objectId: 12 });
  assert.equal(captureOptionsUsed({ x: 1, y: 2 }), false);
  assert.equal(captureOptionsUsed({ x: 1, grid: false }), true);
});

test('a region must be positive and, when the window is known, inside it', () => {
  assert.deepEqual(validateRegion([0, 0, 2560, 1440], SCREEN), [0, 0, 2560, 1440]);
  assert.throws(() => validateRegion([0, 0, 0, 10]), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => validateRegion([0, 0, 10]), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => validateRegion([0, 0, Number.NaN, 10]), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => validateRegion([-1, 0, 10, 10], SCREEN), { code: 'REGION_OUTSIDE_SCREEN' });
  assert.throws(() => validateRegion([2500, 0, 100, 10], SCREEN), { code: 'REGION_OUTSIDE_SCREEN' });
  assert.throws(() => validateRegion([0, 1400, 10, 100], SCREEN), { code: 'REGION_OUTSIDE_SCREEN' });
  assert.deepEqual(validateRegion([2500, 0, 100, 10]), [2500, 0, 100, 10], 'without a window nothing to compare with: the runtime checks');
});

test('screen_capture arguments are checked and cleaned', () => {
  assert.deepEqual(validateCaptureArgs('screen_capture', { maxSize: 800, grid: true, region: [10, 20, 300, 200], timeoutMs: 500, junk: 1 }),
    { maxSize: 800, grid: true, region: [10, 20, 300, 200], timeoutMs: 500 });
  assert.throws(() => validateCaptureArgs('screen_capture', { timeoutMs: 99 }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => validateCaptureArgs('screen_capture', { captureBefore: true }), { message: /pointer tools/ });
});

test('a gesture takes capture options only with capture=true, and captureBefore implies it', () => {
  const plan = buildClickPlan({ x: 5, y: 6, capture: true, maxSize: 800, grid: true, region: [0, 0, 100, 100] });
  assert.equal(plan.capture, true);
  assert.deepEqual([plan.maxSize, plan.grid, plan.region], [800, true, [0, 0, 100, 100]]);
  assert.throws(() => buildClickPlan({ x: 5, y: 6, maxSize: 800 }), { code: 'INVALID_ARGUMENT', message: /need capture=true/ });
  assert.throws(() => buildClickPlan({ x: 5, y: 6, capture: false, grid: true }), { code: 'INVALID_ARGUMENT' });
  assert.equal(buildClickPlan({ x: 5, y: 6, captureBefore: true }).capture, true);
  assert.equal(buildClickPlan({ x: 5, y: 6 }).capture, undefined);
  assert.throws(() => buildClickPlan({ x: 5, y: 6, capture: true, maxSize: 100 }), { code: 'INVALID_ARGUMENT' });
});

test('screen_capture is a read tool that needs capture-tools only when an option is used', () => {
  const tool = byName.warudo_screen_capture;
  assert.equal(tool.kind, 'read');
  assert.equal(tool.needsLease, false);
  assert.equal(tool.requiresCapture, true);
  assert.equal(tool.inputSchema.properties.maxSize.default, 1600);
  assert.deepEqual(tool.inputSchema.properties.space.enum, ['pixels', 'image']);
  assert.match(tool.description, /space="image"/);
});

async function runtimeWith(handler, features = ['pointer', 'capture-tools']) {
  return startFakeRuntime({ handler: (op, params, owner) => (op === 'status' ? { version: 'test', features } : handler(op, params, owner)) });
}

const MAPPING = { window: [2560, 1440], crop: [800, 300, 800, 600], image: [400, 300], viewport: [0, 0, 400, 300], scale: 0.5, clickable: true };

test('a capture reply gets an imageId and space=image clicks land on the window pixel', async () => {
  const runtime = await runtimeWith(async (op) => {
    if (op === 'screen.capture') {
      const file = path.join(runtime.root, 'evidence', 'shot.png');
      await fs.writeFile(file, Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
      return { path: file, width: 2560, height: 1440, mapping: MAPPING, crop: [800, 300, 800, 600] };
    }
    if (op === 'pointer.play') return { gesture: 'click' };
    return {};
  });
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const shot = await call(server, 1, 'warudo_screen_capture', { region: [800, 300, 800, 600], maxSize: 400, grid: true });
    assert.equal(shot.result.isError, false, shot.result.content[0].text);
    const data = payload(shot);
    assert.match(data.imageId, /^img-/);
    assert.equal(shot.result.content[1].type, 'image');
    const sent = runtime.seen.find((request) => request.operation === 'screen.capture').params;
    assert.deepEqual(sent, { region: [800, 300, 800, 600], maxSize: 400, grid: true }, 'the cleaned options reach the runtime');
    // Latest image by default.
    const click = await call(server, 2, 'warudo_pointer_click', { x: 100, y: 50, space: 'image' });
    assert.equal(click.result.isError, false, click.result.content[0].text);
    const play = runtime.seen.find((request) => request.operation === 'pointer.play');
    assert.deepEqual(play.params.steps[0], { f: 0, x: 1000, y: 400 });
    // A drag in image space, with an explicit id; a point outside the image never reaches WARUDO.
    const drag = await call(server, 3, 'warudo_pointer_drag', { space: 'image', imageId: data.imageId, path: [[0, 0], [400, 300]], frames: 2 });
    assert.equal(drag.result.isError, false, drag.result.content[0].text);
    const outside = await call(server, 4, 'warudo_pointer_click', { x: 500, y: 50, space: 'image' });
    assert.equal(payload(outside).code, 'IMAGE_POINT_OUTSIDE');
    const stale = await call(server, 5, 'warudo_pointer_click', { x: 5, y: 5, space: 'image', imageId: 'img-dead-1' });
    assert.equal(payload(stale).code, 'IMAGE_UNKNOWN');
    assert.equal(runtime.seen.filter((request) => request.operation === 'pointer.play').length, 2);
  } finally {
    await runtime.stop();
  }
});

test('space=image with no image yet is refused before anything runs', async () => {
  const runtime = await runtimeWith(() => ({}));
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const reply = await call(server, 1, 'warudo_pointer_click', { x: 5, y: 5, space: 'image' });
    assert.equal(payload(reply).code, 'IMAGE_UNKNOWN');
    assert.equal(runtime.seen.length, 0);
  } finally {
    await runtime.stop();
  }
});

test('a gesture capture carries its own imageId, and capture options need the capture-tools feature', async () => {
  const runtime = await runtimeWith(async (op) => {
    if (op !== 'pointer.play') return {};
    const file = path.join(runtime.root, 'evidence', 'g.png');
    await fs.writeFile(file, Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
    return { gesture: 'click', capture: { path: file, mapping: MAPPING } };
  });
  try {
    const server = createServer({ home: runtime.home, timeoutMs: 3000 });
    const reply = await call(server, 1, 'warudo_pointer_click', { x: 5, y: 5, capture: true, maxSize: 600 });
    assert.match(payload(reply).capture.imageId, /^img-/);
    assert.equal(payload(reply).capture.mapping.crop[0], 800);
    assert.equal(reply.result.content[1].type, 'image');
    assert.equal(runtime.seen.find((request) => request.operation === 'pointer.play').params.maxSize, 600);
  } finally {
    await runtime.stop();
  }
  const older = await runtimeWith(() => ({}), ['pointer']);
  try {
    const server = createServer({ home: older.home, timeoutMs: 3000 });
    const plain = await call(server, 1, 'warudo_pointer_click', { x: 5, y: 5 });
    assert.equal(plain.result.isError, false, 'no capture option: an older runtime still works');
    for (const [name, args] of [['warudo_pointer_click', { x: 5, y: 5, capture: true, grid: true }], ['warudo_screen_capture', { maxSize: 800 }]]) {
      const refused = await call(server, 2, name, args);
      assert.equal(payload(refused).code, 'RUNTIME_OUTDATED', name);
      assert.match(payload(refused).message, /"capture-tools"/);
    }
    const bare = await call(server, 3, 'warudo_screen_capture', {});
    assert.notEqual(payload(bare).code, 'RUNTIME_OUTDATED', 'plain screen_capture needs no new feature');
  } finally {
    await older.stop();
  }
});

test('diff, probe and sequence are read tools that need capture-tools; the image ones come back as images', () => {
  for (const name of ['warudo_screen_diff', 'warudo_screen_probe', 'warudo_screen_sequence', 'warudo_ui_capture']) {
    assert.equal(byName[name].kind, 'read', name);
    assert.equal(byName[name].needsLease, false, name);
    assert.equal(byName[name].requires, 'capture-tools', name);
    assert.ok(byName[name].check, name);
  }
  assert.deepEqual(byName.warudo_screen_diff.inputSchema.required, ['baseline']);
  assert.equal(byName.warudo_screen_diff.inputSchema.properties.threshold.default, 6);
  assert.equal(byName.warudo_screen_sequence.inputSchema.properties.frames.maximum, 16);
  assert.equal(byName.warudo_screen_sequence.inputSchema.properties.everyFrames.maximum, 120);
  assert.equal(byName.warudo_screen_probe.inputSchema.properties.points.maxItems, 64);
});

test('sequence limits: 2..16 frames, every 1..120, at most 1200 rendered frames in all', () => {
  assert.deepEqual(validateCaptureArgs('screen_sequence', { frames: 16, everyFrames: 80 }), { frames: 16, everyFrames: 80 });
  assert.deepEqual(validateCaptureArgs('screen_sequence', {}), {});
  for (const bad of [{ frames: 1 }, { frames: 17 }, { everyFrames: 0 }, { everyFrames: 121 }, { frames: 2.5 }]) {
    assert.throws(() => validateCaptureArgs('screen_sequence', bad), { code: 'INVALID_ARGUMENT' }, JSON.stringify(bad));
  }
  assert.throws(() => validateCaptureArgs('screen_sequence', { frames: 16, everyFrames: 120 }), { code: 'SEQUENCE_TOO_LONG' });
  assert.throws(() => validateCaptureArgs('screen_sequence', { frames: 16, everyFrames: 81 }), { code: 'SEQUENCE_TOO_LONG' });
});

test('diff options and probe arguments are checked', () => {
  const evidencePath = (candidate) => candidate;
  assert.deepEqual(validateCaptureArgs('screen_diff', { baseline: 'a.png', threshold: 0, mergeGap: 64 }, { evidencePath }), { baseline: 'a.png', threshold: 0, mergeGap: 64 });
  for (const bad of [{}, { baseline: '' }, { baseline: 'a.png', threshold: 256 }, { baseline: 'a.png', threshold: -1 }, { baseline: 'a.png', mergeGap: 65 }]) {
    assert.throws(() => validateCaptureArgs('screen_diff', bad, { evidencePath }), { code: 'INVALID_ARGUMENT' }, JSON.stringify(bad));
  }
  assert.throws(() => validateCaptureArgs('screen_probe', {}), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => validateCaptureArgs('screen_probe', { points: [] }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => validateCaptureArgs('screen_probe', { points: [[1]] }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => validateCaptureArgs('screen_probe', { points: Array.from({ length: 65 }, () => [1, 1]) }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => validateCaptureArgs('screen_probe', { regions: [[1, 1, 0, 5]] }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => validateCaptureArgs('screen_probe', { points: [[2560, 5]] }, { screen: SCREEN }), { code: 'REGION_OUTSIDE_SCREEN' });
  assert.throws(() => validateCaptureArgs('screen_probe', { regions: [[2500, 5, 100, 5]] }, { screen: SCREEN }), { code: 'REGION_OUTSIDE_SCREEN' });
  assert.throws(() => validateCaptureArgs('screen_probe', { points: [[1, 1]], radius: 33 }), { code: 'INVALID_ARGUMENT' });
  assert.deepEqual(validateCaptureArgs('screen_probe', { points: [[10, 20]], regions: [[0, 0, 5, 5]], radius: 0 }), { points: [[10, 20]], regions: [[0, 0, 5, 5]], radius: 0 });
});

test('a baseline outside the evidence folder is refused before anything runs', async () => {
  const rt = await runtimeWith((op) => ({ op }));
  try {
    const server = createServer({ home: rt.home, timeoutMs: 3000 });
    const evidence = path.join(rt.root, 'evidence');
    const outsideCandidates = [
      path.join(evidence, '..', 'state.json'),
      path.join(evidence, '..', 'x.png'),
      path.join(evidence, '..', '..', 'secret.png'),
      path.join(evidence, 'sub', '..', '..', 'x.png'),
      path.join(rt.home, 'other.png'),
      path.join(evidence, 'notes.txt'),
      'relative-name.png',
    ];
    for (const baseline of outsideCandidates) {
      const reply = await call(server, 1, 'warudo_screen_diff', { baseline });
      assert.equal(reply.result.isError, true, baseline);
      assert.equal(payload(reply).code, 'EVIDENCE_PATH_REJECTED', baseline);
    }
    assert.equal(rt.seen.length, 0, 'the runtime never saw a rejected baseline');
    const fine = path.join(evidence, 'a.png');
    const ok = await call(server, 2, 'warudo_screen_diff', { baseline: fine });
    assert.equal(ok.result.isError, false, ok.result.content[0].text);
    assert.equal(rt.seen.find((request) => request.operation === 'screen.diff').params.baseline, path.resolve(fine));
  } finally {
    await rt.stop();
  }
});

test('diff and sequence replies carry the image, probe stays text', async () => {
  const rt = await runtimeWith(async (op) => {
    if (op === 'screen.diff' || op === 'screen.sequence') {
      const file = path.join(rt.root, 'evidence', `${op}.png`);
      await fs.writeFile(file, Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
      return { path: file, mapping: op === 'screen.sequence' ? { ...MAPPING, clickable: false } : MAPPING };
    }
    return { items: [] };
  });
  try {
    const server = createServer({ home: rt.home, timeoutMs: 3000 });
    const diff = await call(server, 1, 'warudo_screen_diff', { baseline: path.join(rt.root, 'evidence', 'a.png') });
    assert.equal(diff.result.content[1].type, 'image');
    const sequence = await call(server, 2, 'warudo_screen_sequence', { frames: 6, everyFrames: 10 });
    assert.equal(sequence.result.content[1].type, 'image');
    assert.match(payload(sequence).imageId, /^img-/);
    // A contact sheet cannot be clicked into.
    const refused = await call(server, 3, 'warudo_pointer_click', { x: 5, y: 5, space: 'image' });
    assert.equal(payload(refused).code, 'IMAGE_NOT_CLICKABLE');
    const probe = await call(server, 4, 'warudo_screen_probe', { points: [[10, 10]] });
    assert.equal(probe.result.content.length, 1);
    assert.equal(rt.seen.find((request) => request.operation === 'screen.probe').params.radius, undefined);
    // Probe points can be given in image space (the diff image is clickable).
    const first = await call(server, 5, 'warudo_screen_diff', { baseline: path.join(rt.root, 'evidence', 'a.png') });
    assert.equal(first.result.isError, false);
    await call(server, 6, 'warudo_screen_probe', { points: [[100, 50]], space: 'image' });
    assert.deepEqual(rt.seen.filter((request) => request.operation === 'screen.probe').at(-1).params.points, [[1000, 400]]);
  } finally {
    await rt.stop();
  }
});
