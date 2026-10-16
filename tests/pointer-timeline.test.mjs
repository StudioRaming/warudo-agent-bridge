import assert from 'node:assert/strict';
import test from 'node:test';
import {
  MAX_FRAMES, buildButtonPlan, buildClickPlan, buildDragPlan, buildKeyPlan, buildMovePlan, buildPointerPlan, buildWheelPlan,
  clickPoint, expandTimeline, resolveUiTarget,
} from '../src/pointer-timeline.mjs';
import { MAX_REQUEST_BYTES } from '../src/transport.mjs';

const SCREEN = { width: 1920, height: 1080 };
const downs = (rows) => rows.flatMap((row, frame) => (row.down ? [frame] : []));
const ups = (rows) => rows.flatMap((row, frame) => (row.up ? [frame] : []));
const fails = (fn, code) => assert.throws(fn, (error) => error.code === code, `expected ${code}`);

test('click: hover one frame, press exactly one frame, hold, release exactly one frame', () => {
  const plan = buildClickPlan({ x: 100.25, y: 50 });
  const { rows, post } = expandTimeline(plan);
  assert.equal(plan.frames, 4);
  assert.equal(rows.length, 4);
  assert.deepEqual(rows.map((row) => row.buttons), [0, 1, 1, 0]);
  assert.deepEqual(downs(rows), [1]);
  assert.deepEqual(ups(rows), [3]);
  assert.ok(rows.every((row) => row.x === 100.25 && row.y === 50), 'the pointer stays on the point');
  assert.equal(rows[0].down, 0, 'the hover frame carries no press');
  assert.equal(post.buttons, 0);
});

test('click: holdFrames moves the release, button picks the mask', () => {
  const { rows } = expandTimeline(buildClickPlan({ x: 1, y: 2, holdFrames: 5, button: 'right' }));
  assert.deepEqual(downs(rows), [1]);
  assert.deepEqual(ups(rows), [6]);
  assert.equal(rows[1].buttons, 2);
  assert.equal(buildClickPlan({ x: 1, y: 2, button: 'middle' }).steps[1].set, 4);
});

test('double click: two presses inside six frames of each other', () => {
  const plan = buildClickPlan({ x: 10, y: 10, count: 2 });
  const { rows } = expandTimeline(plan);
  const pressed = downs(rows);
  assert.equal(pressed.length, 2);
  assert.ok(pressed[1] - pressed[0] <= 6, `second press ${pressed[1] - pressed[0]} frames after the first`);
  assert.equal(ups(rows).length, 2);
  assert.ok(ups(rows)[0] < pressed[1], 'the first release comes before the second press');
  fails(() => buildClickPlan({ x: 1, y: 1, count: 2, holdFrames: 5 }), 'INVALID_ARGUMENT');
  fails(() => buildClickPlan({ x: 1, y: 1, count: 3 }), 'INVALID_ARGUMENT');
});

test('modifiers are held on every frame of the gesture and let go right after', () => {
  const { rows } = expandTimeline(buildClickPlan({ x: 1, y: 1, modifiers: ['ctrl', 'alt', 'ctrl'] }));
  for (const [frame, row] of rows.entries()) assert.deepEqual([...row.keys].sort(), ['LeftAlt', 'LeftControl'], `frame ${frame}`);
  assert.deepEqual([...rows[0].keysDown].sort(), ['LeftAlt', 'LeftControl'], 'they go down before the press');
  assert.deepEqual(rows.slice(1).flatMap((row) => row.keysDown), []);
  const drag = expandTimeline(buildDragPlan({ path: [[0, 0], [10, 0]], modifiers: ['shift'] }));
  assert.ok(drag.rows.every((row) => row.keys.includes('LeftShift')));
  assert.deepEqual(drag.post.keys, [], 'modifiers are not left held after the gesture');
  fails(() => buildClickPlan({ x: 1, y: 1, modifiers: ['meta'] }), 'INVALID_ARGUMENT');
});

test('drag: hover, press on the first point, one step per frame, release on the last point', () => {
  const plan = buildDragPlan({ path: [[0, 0], [60, 0], [60, 30]], frames: 3 });
  const { rows } = expandTimeline(plan);
  assert.equal(plan.frames, 3 + 2 * 3);
  assert.deepEqual(downs(rows), [1]);
  assert.deepEqual(ups(rows), [plan.frames - 1]);
  assert.deepEqual([rows[0].x, rows[0].y], [0, 0]);
  assert.deepEqual([rows[1].x, rows[1].y], [0, 0], 'the press happens on the first point');
  assert.deepEqual([rows[2].x, rows[2].y], [20, 0]);
  assert.deepEqual([rows[4].x, rows[4].y], [60, 0], 'a corner is hit exactly');
  const last = rows[rows.length - 1];
  assert.deepEqual([last.x, last.y], [60, 30], 'released on the last point');
  assert.deepEqual([rows[rows.length - 2].x, rows[rows.length - 2].y], [60, 30], 'the last point is reached before the release');
  assert.ok(rows.slice(1, -1).every((row) => row.buttons === 1), 'the button stays down all the way');
  for (let f = 2; f < rows.length - 1; f += 1) {
    assert.notDeepEqual([rows[f].x, rows[f].y], [rows[f - 1].x, rows[f - 1].y], `frame ${f} moves`);
  }
});

test('drag: totalFrames spreads frames by path length and still ends exactly on the last point', () => {
  const plan = buildDragPlan({ path: [[0, 0], [100, 0], [100, 10]], totalFrames: 11 });
  const { rows } = expandTimeline(plan);
  assert.equal(plan.frames, 14);
  const moves = rows.slice(2, 13);
  assert.equal(moves.length, 11);
  assert.deepEqual([moves[0].x, moves[0].y], [10, 0], 'the 110 px path is covered 10 px per frame');
  assert.deepEqual([moves[9].x, moves[9].y], [100, 0]);
  assert.deepEqual([moves[10].x, moves[10].y], [100, 10]);
  const distance = moves.slice(1).map((row, index) => Math.hypot(row.x - moves[index].x, row.y - moves[index].y));
  for (const step of distance) assert.ok(Math.abs(step - 10) < 0.01, `even speed, got ${step}`);
});

test('drag: a path that never moves still presses and releases on its point', () => {
  const { rows } = expandTimeline(buildDragPlan({ path: [[5, 5], [5, 5]], totalFrames: 2 }));
  assert.ok(rows.every((row) => row.x === 5 && row.y === 5));
  assert.deepEqual(downs(rows), [1]);
});

test('wheel: notches add up to the request, sign follows direction, hover comes first', () => {
  for (const notches of [3, -3, 7, -20, 1]) {
    for (const frames of [undefined, 1, 4]) {
      const plan = buildWheelPlan({ x: 10, y: 10, notches, frames });
      const { rows } = expandTimeline(plan);
      assert.equal(rows.reduce((sum, row) => sum + row.wheel, 0), notches, `${notches} over ${frames}`);
      assert.equal(rows[0].wheel, 0, 'no turn on the hover frame');
      assert.equal(plan.frames, (frames ?? Math.abs(notches)) + 1);
      assert.ok(rows.every((row) => Math.sign(row.wheel) === 0 || Math.sign(row.wheel) === Math.sign(notches)));
    }
  }
  const spread = expandTimeline(buildWheelPlan({ x: 1, y: 1, notches: 5, frames: 2 })).rows.map((row) => row.wheel);
  assert.deepEqual(spread, [0, 2, 3]);
  fails(() => buildWheelPlan({ x: 1, y: 1, notches: 0 }), 'INVALID_ARGUMENT');
  fails(() => buildWheelPlan({ x: 1, y: 1, notches: 21 }), 'INVALID_ARGUMENT');
  fails(() => buildWheelPlan({ x: 1, y: 1, notches: 1.5 }), 'INVALID_ARGUMENT');
});

test('normalized coordinates become pixels when the window size is given, and stay normalized for the runtime otherwise', () => {
  const plan = buildClickPlan({ x: 0.5, y: 0.25, space: 'normalized' }, { screen: SCREEN });
  assert.equal(plan.space, 'pixels');
  assert.deepEqual([plan.steps[0].x, plan.steps[0].y], [960, 270]);
  const drag = buildDragPlan({ path: [[0, 0], [1, 1]], space: 'normalized', frames: 1 }, { screen: SCREEN });
  assert.deepEqual(drag.steps.at(-2), { f: 2, x: 1920, y: 1080 });
  const open = buildClickPlan({ x: 0.5, y: 0.25, space: 'normalized' });
  assert.equal(open.space, 'normalized');
  assert.deepEqual([open.steps[0].x, open.steps[0].y], [0.5, 0.25]);
  assert.deepEqual(expandTimeline(open, {}, SCREEN).rows[0].x, 960);
  assert.throws(() => expandTimeline(open), (error) => error.code === 'INVALID_ARGUMENT', 'a normalized plan cannot be expanded without a window size');
  fails(() => buildClickPlan({ x: 1.2, y: 0, space: 'normalized' }), 'INVALID_ARGUMENT');
});

test('limits: 600 frames, 64 points, known spaces and buttons, finite numbers', () => {
  const line = (count) => Array.from({ length: count }, (_, index) => [index, 0]);
  assert.equal(buildDragPlan({ path: line(11), frames: 59 }).frames, 593);
  fails(() => buildDragPlan({ path: line(11), frames: 60 }), 'GESTURE_TOO_LONG');
  assert.equal(buildDragPlan({ path: [[0, 0], [9, 0]], totalFrames: 597 }).frames, MAX_FRAMES);
  fails(() => buildDragPlan({ path: [[0, 0], [9, 0]], totalFrames: 598 }), 'INVALID_ARGUMENT');
  assert.equal(buildDragPlan({ path: line(64), frames: 1 }).frames, 3 + 63);
  fails(() => buildDragPlan({ path: line(65), frames: 1 }), 'INVALID_ARGUMENT');
  fails(() => buildDragPlan({ path: [[0, 0]] }), 'INVALID_ARGUMENT');
  fails(() => buildDragPlan({ path: [[0, 0], [1]] }), 'INVALID_ARGUMENT');
  fails(() => buildClickPlan({ x: 1, y: 1, space: 'screen' }), 'INVALID_ARGUMENT');
  fails(() => buildClickPlan({ x: 1, y: 1, button: 'constructor' }), 'INVALID_ARGUMENT');
  fails(() => buildClickPlan({ x: Number.NaN, y: 1 }), 'INVALID_ARGUMENT');
  fails(() => buildClickPlan({ x: 1e9, y: 1 }), 'INVALID_ARGUMENT');
  fails(() => buildClickPlan({ x: 1 }), 'INVALID_ARGUMENT');
  fails(() => buildMovePlan({ x: 1, y: 1, frames: 121 }), 'INVALID_ARGUMENT');
  fails(() => expandTimeline({ frames: 601, steps: [] }), 'GESTURE_TOO_LONG');
});

test('move: hover only, keep is passed on, no button ever goes down', () => {
  const plan = buildMovePlan({ x: 30, y: 40, frames: 4, keep: true });
  const { rows } = expandTimeline(plan);
  assert.equal(rows.length, 4);
  assert.equal(plan.keep, true);
  assert.ok(rows.every((row) => row.buttons === 0 && row.down === 0 && row.x === 30 && row.y === 40));
  assert.equal(buildMovePlan({ x: 1, y: 1 }).keep, false);
});

test('button down and up, alone or with a move first, work on top of what an earlier call left held', () => {
  const down = buildButtonPlan({ action: 'down', x: 5, y: 6 });
  assert.equal(down.frames, 2);
  const first = expandTimeline(down);
  assert.deepEqual(downs(first.rows), [1]);
  assert.equal(first.post.buttons, 1, 'a button left down stays down');
  const held = { x: first.post.x, y: first.post.y, buttons: first.post.buttons, keys: [] };
  const move = expandTimeline(buildMovePlan({ x: 50, y: 60, frames: 2, keep: true }), held);
  assert.ok(move.rows.every((row) => row.buttons === 1 && row.down === 0 && row.up === 0), 'moving while held is a drag, not a new press');
  const up = expandTimeline(buildButtonPlan({ action: 'up' }), { x: 50, y: 60, buttons: 1, keys: [] });
  assert.deepEqual(ups(up.rows), [0]);
  assert.equal(up.post.buttons, 0);
  assert.deepEqual([up.rows[0].x, up.rows[0].y], [50, 60], 'no x/y keeps the parked position');
  fails(() => buildButtonPlan({ action: 'down', x: 1 }), 'INVALID_ARGUMENT');
  fails(() => buildButtonPlan({ action: 'toggle' }), 'INVALID_ARGUMENT');
  assert.equal(expandTimeline(buildClickPlan({ x: 1, y: 1 }), { x: 9, y: 9, buttons: 1, keys: [] }).rows[1].down, 0, 'an already held button is not pressed again');
});

test('virtual keys: press holds for the frames, down stays down, up lets go, names are checked', () => {
  const press = expandTimeline(buildKeyPlan({ key: 'Space', frames: 3 }));
  assert.deepEqual(press.rows.map((row) => row.keys.length), [1, 1, 1, 0]);
  assert.deepEqual(press.rows[0].keysDown, ['Space']);
  assert.deepEqual(press.rows[3].keysUp, ['Space']);
  const down = expandTimeline(buildKeyPlan({ key: 'A', action: 'down' }));
  assert.deepEqual(down.post.keys, ['A']);
  const up = expandTimeline(buildKeyPlan({ key: 'A', action: 'up' }), { keys: ['A', 'B'] });
  assert.deepEqual(up.post.keys, ['B']);
  assert.deepEqual(up.rows[0].keysUp, ['A']);
  fails(() => buildKeyPlan({ key: 'Ctrl+C' }), 'INVALID_ARGUMENT');
  fails(() => buildKeyPlan({ key: 'Space', action: 'tap' }), 'INVALID_ARGUMENT');
  fails(() => buildPointerPlan('nope', {}), undefined);
});

test('an unsorted step or one past the last frame is rejected by the reference expansion', () => {
  const base = { gesture: 'x', frames: 3, space: 'pixels' };
  assert.throws(() => expandTimeline({ ...base, steps: [{ f: 2 }, { f: 1 }] }), (error) => error.code === 'INVALID_ARGUMENT');
  assert.throws(() => expandTimeline({ ...base, steps: [{ f: 5 }] }), (error) => error.code === 'INVALID_ARGUMENT');
});

const item = (over) => ({ id: 1, path: 'Canvas/A', label: 'Apply', interactable: true, onScreen: true, rect: [0, 0, 10, 10], ...over });

test('ui target by id, by label (exact and contains, any case) and by index', () => {
  const items = [item({ id: 1, label: 'Apply' }), item({ id: 2, label: 'Apply all' }), item({ id: 3, label: 'Cancel' })];
  assert.equal(resolveUiTarget(items, { id: 3 }).id, 3);
  assert.equal(resolveUiTarget(items, { label: 'cancel' }).id, 3);
  assert.equal(resolveUiTarget(items, { label: ' APPLY ', labelMatch: 'exact' }).id, 1);
  assert.equal(resolveUiTarget(items, { label: 'all' }).id, 2);
  assert.equal(resolveUiTarget(items, { label: 'apply', index: 1 }).id, 2);
  assert.equal(resolveUiTarget(items, { label: 'apply', index: 0 }).id, 1);
});

test('ui target: not found, ambiguous, out of range, and unusable matches never win', () => {
  const items = [item({ id: 1, label: 'Apply' }), item({ id: 2, label: 'Apply all' }), item({ id: 4, label: 'Save', interactable: false }), item({ id: 5, label: 'Save', onScreen: false })];
  fails(() => resolveUiTarget(items, { label: 'missing' }), 'UI_NOT_FOUND');
  fails(() => resolveUiTarget(items, { id: 99 }), 'UI_NOT_FOUND');
  assert.throws(() => resolveUiTarget(items, { label: 'apply' }), (error) => error.code === 'UI_AMBIGUOUS' && error.details.candidates.length === 2);
  fails(() => resolveUiTarget(items, { label: 'apply', index: 2 }), 'INVALID_ARGUMENT');
  fails(() => resolveUiTarget(items, { label: 'save' }), 'UI_NOT_CLICKABLE');
  fails(() => resolveUiTarget(items, { id: 4 }), 'UI_NOT_CLICKABLE');
  const twin = [item({ id: 7, label: 'Go', interactable: false }), item({ id: 8, label: 'Go' })];
  assert.equal(resolveUiTarget(twin, { label: 'go' }).id, 8, 'the disabled twin is not a candidate');
  fails(() => resolveUiTarget(items, {}), 'INVALID_ARGUMENT');
  fails(() => resolveUiTarget(items, { id: 1, label: 'x' }), 'INVALID_ARGUMENT');
  fails(() => resolveUiTarget(items, { label: 'x', labelMatch: 'fuzzy' }), 'INVALID_ARGUMENT');
  fails(() => resolveUiTarget(items, { label: '  ' }), 'INVALID_ARGUMENT');
  fails(() => resolveUiTarget(items, { label: 'apply', index: -1 }), 'INVALID_ARGUMENT');
});

test('click point is the middle of the rectangle, clipped to the window', () => {
  assert.deepEqual(clickPoint({ rect: [100, 200, 40, 20] }, SCREEN), [120, 210]);
  assert.deepEqual(clickPoint({ rect: [-50, 0, 100, 10] }, SCREEN), [25, 5]);
  assert.deepEqual(clickPoint({ rect: [1900, 1070, 100, 100] }, SCREEN), [1910, 1075]);
});

test('the longest gesture still fits in one mailbox request', () => {
  const path = Array.from({ length: 64 }, (_, index) => [12345.678 + index * 3.141, 9876.543 - index * 2.718]);
  const plan = buildDragPlan({ path, totalFrames: 597, modifiers: ['ctrl', 'shift', 'alt'], capture: true, expectObject: -2147483648, settleFrames: 60 });
  assert.equal(plan.frames, MAX_FRAMES);
  const request = JSON.stringify({ protocol: 2, requestId: 'x'.repeat(32), sessionId: 'x'.repeat(32), createdUtc: new Date().toISOString(), owner: 'o'.repeat(128), operation: 'pointer.play', params: plan });
  assert.ok(Buffer.byteLength(request) < MAX_REQUEST_BYTES / 2, `${Buffer.byteLength(request)} bytes`);
});

test('ui target by path finds an icon button that has no text, with the same usable-match and index rules', () => {
  const items = [
    item({ id: 1, path: 'Canvas/Menu/btn:menu.undo', label: '' }),
    item({ id: 2, path: 'Canvas/Menu/btn:menu.redo', label: null }),
    item({ id: 3, path: 'Canvas/Panel/btn:menu.undo', label: '', interactable: false }),
    item({ id: 4, path: 'Canvas/Other/Row1', label: 'Row' }),
    item({ id: 5, path: 'Canvas/Other/Row2', label: 'Row' }),
  ];
  assert.equal(resolveUiTarget(items, { path: 'btn:menu.undo' }).id, 1, 'the disabled twin never wins');
  assert.equal(resolveUiTarget(items, { path: ' BTN:MENU.REDO ' }).id, 2, 'case-insensitive and trimmed');
  fails(() => resolveUiTarget(items, { path: 'btn:menu.nothing' }), 'UI_NOT_FOUND');
  assert.throws(() => resolveUiTarget(items, { path: 'canvas/other/row' }), (error) => error.code === 'UI_AMBIGUOUS' && error.details.candidates.length === 2);
  assert.equal(resolveUiTarget(items, { path: 'canvas/other/row', index: 1 }).id, 5);
  fails(() => resolveUiTarget([items[2]], { path: 'btn:menu.undo' }), 'UI_NOT_CLICKABLE');
  fails(() => resolveUiTarget(items, { path: 'btn', label: 'Apply' }), 'INVALID_ARGUMENT');
  fails(() => resolveUiTarget(items, { path: '  ' }), 'INVALID_ARGUMENT');
});

test('wheel plan carries expectObject and expectScroll to the runtime, and expectScroll is only for a wheel', () => {
  const plain = buildWheelPlan({ x: 10, y: 10, notches: 2 });
  assert.equal(plain.expectObject, undefined);
  assert.equal(plain.expectScroll, undefined, 'default behavior unchanged');
  const guarded = buildWheelPlan({ x: 10, y: 10, notches: 2, expectObject: 77, expectScroll: true });
  assert.equal(guarded.expectObject, 77);
  assert.equal(guarded.expectScroll, true);
  assert.equal(buildWheelPlan({ x: 10, y: 10, notches: 2, expectScroll: false }).expectScroll, undefined);
  fails(() => buildWheelPlan({ x: 10, y: 10, notches: 2, expectScroll: 'yes' }), 'INVALID_ARGUMENT');
  fails(() => buildClickPlan({ x: 1, y: 1, expectScroll: true }), 'INVALID_ARGUMENT');
});
