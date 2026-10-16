import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { createServer } from '../src/mcp-server.mjs';
import { resolveUiTarget } from '../src/pointer-timeline.mjs';
import { legendFrom, listParamsFor, resolveMark } from '../src/ui-marks.mjs';
import { startFakeRuntime } from './helpers/fake-runtime.mjs';

const call = (server, id, name, args = {}) => server.handle({ jsonrpc: '2.0', id, method: 'tools/call', params: { name, arguments: args } });
const payload = (reply) => JSON.parse(reply.result.content[0].text);
const SCREEN = { width: 2560, height: 1440 };
const row = (id, label, over = {}) => ({ id, path: `Canvas/${label}`, label, kind: 'button', rect: [100 * id, 200, 40, 20], interactable: true, onScreen: true, handlers: ['Button'], ...over });
const CROP = [0, 0, 2560, 1440];
const MAPPING = { window: [2560, 1440], crop: CROP, image: [1600, 900], viewport: [0, 0, 1600, 900], scale: 0.625, clickable: true };

const reply = (items) => ({ screen: SCREEN, total: items.length, truncated: false, items });

test('legend and mark: the same controls resolve, a changed screen is MARK_STALE', () => {
  const rows = [row(1, 'Apply'), row(2, 'Cancel'), row(3, 'Save')];
  const legend = legendFrom({ screen: SCREEN, crop: CROP, query: 'a', interactableOnly: false, elements: rows.map((item, index) => ({ n: index + 1, id: item.id })), imageId: 'img-x-1' });
  assert.deepEqual(listParamsFor(legend), { query: 'a', interactableOnly: false, region: CROP, limit: 3 });
  assert.equal(resolveMark(legend, 2, reply(rows)).label, 'Cancel');
  assert.throws(() => resolveMark(legend, 2, reply([rows[0], rows[2]])), { code: 'MARK_STALE', message: /1 control\(s\) gone/ });
  assert.throws(() => resolveMark(legend, 2, reply([rows[0], rows[1], row(9, 'New'), rows[2]])), { code: 'MARK_STALE', message: /1 new/ });
  assert.throws(() => resolveMark(legend, 2, reply([rows[1], rows[0], rows[2]])), { code: 'MARK_STALE', message: /order changed/ });
  assert.throws(() => resolveMark(legend, 2, { ...reply(rows), screen: { width: 1920, height: 1080 } }), { code: 'MARK_STALE', message: /window size/ });
});

test('a mark that was never numbered, or no capture at all, is MARK_UNKNOWN; a bad number is INVALID_ARGUMENT', () => {
  const legend = legendFrom({ screen: SCREEN, crop: CROP, elements: [{ n: 1, id: 1 }] });
  assert.throws(() => resolveMark(legend, 2, reply([row(1, 'A')])), { code: 'MARK_UNKNOWN', message: /numbered 1/ });
  assert.throws(() => resolveMark(null, 1, null), { code: 'MARK_UNKNOWN', message: /No warudo_ui_capture yet/ });
  for (const bad of [0, -1, 1.5, '1']) assert.throws(() => resolveMark(legend, bad, reply([row(1, 'A')])), { code: 'INVALID_ARGUMENT' });
  assert.equal(legendFrom({ elements: [] }), null, 'an older runtime has no legend');
});

test('near: an exact label wins over a longer one that contains it; contains is the fallback', () => {
  const items = [row(1, 'Apply all'), row(2, 'Apply'), row(3, 'Reapply now')];
  assert.equal(resolveUiTarget(items, { label: 'apply', labelMatch: 'auto' }).id, 2);
  assert.throws(() => resolveUiTarget(items, { label: 'apply', labelMatch: 'contains' }), { code: 'UI_AMBIGUOUS' });
  assert.equal(resolveUiTarget(items, { label: 'now', labelMatch: 'auto' }).id, 3, 'no exact label: contains');
  // An exact control that cannot be clicked does not hide a usable one that contains the text.
  const disabled = [row(1, 'Save', { interactable: false }), row(2, 'Save as')];
  assert.equal(resolveUiTarget(disabled, { label: 'save', labelMatch: 'auto' }).id, 2);
  assert.throws(() => resolveUiTarget(items, { label: 'x', labelMatch: 'sloppy' }), { code: 'INVALID_ARGUMENT' });
});

async function runtime(handler) {
  return startFakeRuntime({ handler: (op, params, owner) => (op === 'status' ? { version: 'test', features: ['pointer', 'capture-tools'] } : handler(op, params, owner)) });
}

test('ui_capture returns the image, remembers its legend, and ui_click mark clicks that control', async () => {
  let rows = [row(1, 'Apply'), row(2, 'Cancel'), row(3, 'Save')];
  const rt = await runtime(async (op, params) => {
    if (op === 'ui.capture') {
      const file = path.join(rt.root, 'evidence', 'ui.png');
      await fs.writeFile(file, Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
      return { path: file, screen: SCREEN, crop: CROP, mapping: MAPPING, query: params.query ?? '', interactableOnly: true, elements: rows.map((item, index) => ({ n: index + 1, id: item.id, kind: item.kind, label: item.label })) };
    }
    if (op === 'ui.list') return reply(rows);
    if (op === 'pointer.play') return { gesture: 'click' };
    return {};
  });
  try {
    const server = createServer({ home: rt.home, timeoutMs: 3000 });
    const stranger = await call(server, 0, 'warudo_ui_click', { mark: 1 });
    assert.equal(payload(stranger).code, 'MARK_UNKNOWN');
    const shot = await call(server, 1, 'warudo_ui_capture', { maxSize: 1200, limit: 3 });
    assert.equal(shot.result.isError, false, shot.result.content[0].text);
    assert.equal(shot.result.content[1].type, 'image');
    assert.match(payload(shot).imageId, /^img-/);
    assert.deepEqual(rt.seen.find((request) => request.operation === 'ui.capture').params, { maxSize: 1200, limit: 3 });

    const click = await call(server, 2, 'warudo_ui_click', { mark: 2 });
    assert.equal(click.result.isError, false, click.result.content[0].text);
    assert.equal(payload(click).resolved.id, 2);
    const relist = rt.seen.filter((request) => request.operation === 'ui.list').at(-1).params;
    assert.deepEqual(relist, { query: '', interactableOnly: true, region: CROP, limit: 3 }, 'the same filters are listed again');
    const play = rt.seen.find((request) => request.operation === 'pointer.play');
    assert.equal(play.params.expectObject, 2);
    assert.deepEqual(play.params.steps[0], { f: 0, x: 220, y: 210 });

    // The screen changed: the number no longer means the same control.
    rows = [row(1, 'Apply'), row(3, 'Save')];
    const stale = await call(server, 3, 'warudo_ui_click', { mark: 2 });
    assert.equal(payload(stale).code, 'MARK_STALE');
    assert.equal(rt.seen.filter((request) => request.operation === 'pointer.play').length, 1, 'nothing was clicked');
    // Exactly one selector.
    assert.equal(payload(await call(server, 4, 'warudo_ui_click', { mark: 1, near: 'Apply' })).code, 'INVALID_ARGUMENT');
    assert.equal(payload(await call(server, 5, 'warudo_ui_click', {})).code, 'INVALID_ARGUMENT');
  } finally {
    await rt.stop();
  }
});

test('ui_click near clicks the exact text first and works on a plugin overlay row (hidden canvas)', async () => {
  const rt = await runtime((op) => {
    if (op === 'ui.list') return reply([row(1, 'Apply all', { rect: [10, 10, 40, 20] }), row(2, 'Apply', { rect: [100, 10, 40, 20], hidden: true, canvas: '_WcsMenuOverlay' })]);
    if (op === 'pointer.play') return { gesture: 'click' };
    return {};
  });
  try {
    const server = createServer({ home: rt.home, timeoutMs: 3000 });
    const reply1 = await call(server, 1, 'warudo_ui_click', { near: 'apply' });
    assert.equal(reply1.result.isError, false, reply1.result.content[0].text);
    assert.equal(payload(reply1).resolved.id, 2);
    assert.deepEqual(rt.seen.find((request) => request.operation === 'ui.list').params, { query: 'apply', interactableOnly: false, limit: 500 });
  } finally {
    await rt.stop();
  }
});
