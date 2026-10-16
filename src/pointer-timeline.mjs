import { BridgeError } from './transport.mjs';
import { normalizeCaptureOptions } from './capture-options.mjs';

// Builds the frame-stamped timelines that the runtime's pointer.play operation replays into WARUDO's UI, and
// resolves ui.list rows for warudo_ui_click. Pure functions, so the tests cover them without WARUDO.
//
// A plan is { gesture, frames, steps, space, holdKeys?, keep?, settleFrames?, expectObject?, capture? }.
// A step is { f, x?, y?, set?, clear?, keysAdd?, keysDel?, wheel? } and is applied on top of the previous frame:
// x/y move the pointer, set/clear are button masks (left 1, right 2, middle 4), keysAdd/keysDel change held virtual keys,
// wheel is the notches turned on that frame only. holdKeys are down for frames 0..frames-1 (the modifiers of a gesture).
// expandTimeline() is the reference for what the runtime (PointerTimeline.Build in VirtualPointerInput.cs) must do.

export const MAX_FRAMES = 600;
export const MAX_POINTS = 64;
const MAX_COORDINATE = 20_000;
const BUTTON_MASK = { left: 1, right: 2, middle: 4 };
const MODIFIER_KEY = { ctrl: 'LeftControl', shift: 'LeftShift', alt: 'LeftAlt' };
const KEY_NAME = /^[A-Za-z][A-Za-z0-9]{0,31}$/;

const invalid = (message) => new BridgeError('INVALID_ARGUMENT', message);
const round3 = (value) => Math.round(value * 1000) / 1000;

function integer(value, fallback, min, max, name) {
  const number = value === undefined ? fallback : value;
  if (!Number.isInteger(number) || number < min || number > max) throw invalid(`${name} must be an integer ${min}..${max}.`);
  return number;
}

function buttonMask(button = 'left') {
  if (!Object.hasOwn(BUTTON_MASK, button)) throw invalid('button must be left, right or middle.');
  return BUTTON_MASK[button];
}

function holdKeys(modifiers) {
  if (modifiers === undefined) return [];
  if (!Array.isArray(modifiers) || modifiers.length > 3) throw invalid('modifiers must be an array of ctrl, shift, alt.');
  const keys = [];
  for (const name of modifiers) {
    if (!Object.hasOwn(MODIFIER_KEY, name)) throw invalid(`Unknown modifier "${String(name)}"; use ctrl, shift or alt.`);
    if (!keys.includes(MODIFIER_KEY[name])) keys.push(MODIFIER_KEY[name]);
  }
  return keys;
}

// One coordinate pair, checked and (when the window size is known) turned into pixels.
function toPoint(x, y, space, screen, name = 'x/y') {
  if (typeof x !== 'number' || typeof y !== 'number' || !Number.isFinite(x) || !Number.isFinite(y)) throw invalid(`${name} must be finite numbers.`);
  if (space === 'normalized') {
    if (x < 0 || x > 1 || y < 0 || y > 1) throw invalid(`${name}: normalized coordinates are 0..1 of the window.`);
    return screen ? [round3(x * screen.width), round3(y * screen.height)] : [x, y];
  }
  if (Math.abs(x) > MAX_COORDINATE || Math.abs(y) > MAX_COORDINATE) throw invalid(`${name}: pixel coordinates are limited to +-${MAX_COORDINATE}.`);
  return [round3(x), round3(y)];
}

function spaceOf(args) {
  const space = args.space ?? 'pixels';
  if (space !== 'pixels' && space !== 'normalized') throw invalid('space must be pixels or normalized.');
  return space;
}

// Normalized input only stays normalized when the window size is unknown; the runtime then multiplies.
function finish(gesture, args, { steps, frames, space, screen, keys = [], keep }) {
  if (frames > MAX_FRAMES) throw new BridgeError('GESTURE_TOO_LONG', `The gesture needs ${frames} frames; the limit is ${MAX_FRAMES}. Use fewer points, fewer frames per segment or a smaller totalFrames.`);
  const plan = { gesture, frames, steps, space: space === 'normalized' && !screen ? 'normalized' : 'pixels' };
  if (keys.length) plan.holdKeys = keys;
  if (keep !== undefined) plan.keep = keep;
  if (args.settleFrames !== undefined) plan.settleFrames = integer(args.settleFrames, 2, 0, 60, 'settleFrames');
  if (args.expectObject !== undefined) plan.expectObject = integer(args.expectObject, 0, -2_147_483_648, 2_147_483_647, 'expectObject');
  if (args.expectScroll !== undefined && typeof args.expectScroll !== 'boolean') throw invalid('expectScroll must be true or false.');
  if (args.expectScroll === true) {
    if (gesture !== 'wheel') throw invalid('expectScroll only applies to warudo_pointer_wheel.');
    plan.expectScroll = true;
  }
  if (args.capture !== undefined) plan.capture = args.capture === true;
  // Options of the image the gesture returns (maxSize, grid, region, around, padding, markPointer, captureBefore).
  const options = normalizeCaptureOptions(args);
  if (Object.keys(options).length) {
    if (options.captureBefore === true) plan.capture = true;
    if (plan.capture !== true) throw invalid(`${Object.keys(options).join(', ')} shape the captured image and need capture=true.`);
    Object.assign(plan, options);
  }
  return plan;
}

// hover, press, hold, release. A double click presses again two frames after the first release.
export function buildClickPlan(args, { screen } = {}) {
  const space = spaceOf(args);
  const mask = buttonMask(args.button);
  const hold = integer(args.holdFrames, 2, 1, 30, 'holdFrames');
  const count = args.count ?? 1;
  if (count !== 1 && count !== 2) throw invalid('count must be 1 or 2.');
  if (count === 2 && hold > 4) throw invalid('A double click needs holdFrames <= 4 so both presses fall inside the 0.3 s click window.');
  const [x, y] = toPoint(args.x, args.y, space, screen);
  const steps = [{ f: 0, x, y }, { f: 1, set: mask }, { f: 1 + hold, clear: mask }];
  let last = 1 + hold;
  if (count === 2) {
    const second = last + 2;
    steps.push({ f: second, set: mask }, { f: second + hold, clear: mask });
    last = second + hold;
  }
  return finish('click', args, { steps, frames: last + 1, space, screen, keys: holdKeys(args.modifiers) });
}

// move to the first point, hover one frame, press, move a little every frame, release on the last point.
export function buildDragPlan(args, { screen } = {}) {
  const space = spaceOf(args);
  const mask = buttonMask(args.button);
  if (!Array.isArray(args.path) || args.path.length < 2 || args.path.length > MAX_POINTS) throw invalid(`path needs 2..${MAX_POINTS} points.`);
  const points = args.path.map((pair, index) => {
    if (!Array.isArray(pair) || pair.length !== 2) throw invalid(`path[${index}] must be [x, y].`);
    return toPoint(pair[0], pair[1], space, screen, `path[${index}]`);
  });
  const moves = [];
  if (args.totalFrames !== undefined) {
    // Spread over the whole path by length, so a long segment gets more frames than a short one.
    const total = integer(args.totalFrames, 0, 1, MAX_FRAMES - 3, 'totalFrames');
    const lengths = points.slice(1).map((point, index) => Math.hypot(point[0] - points[index][0], point[1] - points[index][1]));
    const sum = lengths.reduce((a, b) => a + b, 0);
    let segment = 0;
    let before = 0;
    for (let frame = 1; frame <= total; frame += 1) {
      if (frame === total) { moves.push(points[points.length - 1]); continue; }
      const distance = (sum * frame) / total;
      while (segment < lengths.length - 1 && before + lengths[segment] < distance) { before += lengths[segment]; segment += 1; }
      const t = lengths[segment] === 0 ? 1 : Math.min(1, Math.max(0, (distance - before) / lengths[segment]));
      const from = points[segment];
      const to = points[segment + 1];
      moves.push([round3(from[0] + (to[0] - from[0]) * t), round3(from[1] + (to[1] - from[1]) * t)]);
    }
  } else {
    const per = integer(args.frames, 6, 1, 60, 'frames');
    for (let segment = 0; segment < points.length - 1; segment += 1) {
      const from = points[segment];
      const to = points[segment + 1];
      for (let step = 1; step <= per; step += 1) {
        moves.push(step === per ? to : [round3(from[0] + ((to[0] - from[0]) * step) / per), round3(from[1] + ((to[1] - from[1]) * step) / per)]);
      }
    }
  }
  const steps = [{ f: 0, x: points[0][0], y: points[0][1] }, { f: 1, set: mask }];
  moves.forEach(([x, y], index) => steps.push({ f: 2 + index, x, y }));
  steps.push({ f: 2 + moves.length, clear: mask });
  return finish('drag', args, { steps, frames: 3 + moves.length, space, screen, keys: holdKeys(args.modifiers) });
}

// Hover only, for hover outlines and tooltips. keep leaves the virtual pointer parked there afterwards.
export function buildMovePlan(args, { screen } = {}) {
  const space = spaceOf(args);
  const frames = integer(args.frames, 2, 1, 120, 'frames');
  const [x, y] = toPoint(args.x, args.y, space, screen);
  return finish('move', args, { steps: [{ f: 0, x, y }], frames, space, screen, keep: args.keep === true });
}

// One button edge, for gestures built from several calls: down, some moves with keep, up.
export function buildButtonPlan(args, { screen } = {}) {
  const space = spaceOf(args);
  const mask = buttonMask(args.button);
  if (args.action !== 'down' && args.action !== 'up') throw invalid('action must be down or up.');
  if ((args.x === undefined) !== (args.y === undefined)) throw invalid('Pass x and y together, or neither to use the parked pointer.');
  const steps = [];
  let frame = 0;
  if (args.x !== undefined) {
    const [x, y] = toPoint(args.x, args.y, space, screen);
    steps.push({ f: 0, x, y });
    frame = 1;
  }
  steps.push(args.action === 'down' ? { f: frame, set: mask } : { f: frame, clear: mask });
  return finish(`button.${args.action}`, args, { steps, frames: frame + 1, space, screen, keys: holdKeys(args.modifiers) });
}

// Wheel notches spread over the frames (up positive). Hover one frame first so the target is under the pointer.
export function buildWheelPlan(args, { screen } = {}) {
  const space = spaceOf(args);
  if (!Number.isInteger(args.notches) || args.notches === 0 || Math.abs(args.notches) > 20) throw invalid('notches must be a non-zero integer, |n| <= 20.');
  const total = Math.abs(args.notches);
  const sign = Math.sign(args.notches);
  const frames = integer(args.frames, total, 1, 60, 'frames');
  const [x, y] = toPoint(args.x, args.y, space, screen);
  const steps = [{ f: 0, x, y }];
  for (let frame = 1; frame <= frames; frame += 1) {
    const turn = Math.floor((total * frame) / frames) - Math.floor((total * (frame - 1)) / frames);
    if (turn) steps.push({ f: frame, wheel: sign * turn });
  }
  return finish('wheel', args, { steps, frames: frames + 1, space, screen, keys: holdKeys(args.modifiers) });
}

// Virtual keys: only code that reads the plugin contract sees them; WARUDO hotkeys use warudo_input_key.
export function buildKeyPlan(args) {
  if (typeof args.key !== 'string' || !KEY_NAME.test(args.key)) throw invalid('key must be a Unity KeyCode name such as Space, A, LeftShift or Return.');
  const action = args.action ?? 'press';
  if (!['press', 'down', 'up'].includes(action)) throw invalid('action must be press, down or up.');
  const hold = integer(args.frames, 2, 1, 60, 'frames');
  const steps = action === 'up' ? [{ f: 0, keysDel: [args.key] }] : [{ f: 0, keysAdd: [args.key] }];
  let frames = 1;
  if (action === 'press') {
    steps.push({ f: hold, keysDel: [args.key] });
    frames = hold + 1;
  }
  return finish(`key.${action}`, args, { steps, frames, space: 'pixels' });
}

const BUILDERS = {
  click: buildClickPlan,
  drag: buildDragPlan,
  move: buildMovePlan,
  button: buildButtonPlan,
  wheel: buildWheelPlan,
  key: buildKeyPlan,
};

export function buildPointerPlan(kind, args, options = {}) {
  const build = BUILDERS[kind];
  if (!build) throw new Error(`Unknown pointer plan "${kind}".`);
  return build(args, options);
}

// The state of every frame of a plan, the same way the runtime computes it. initial is what was held
// from an earlier call: { x, y, buttons, keys }. Positions are in the plan's space (pixels once a screen is given).
export function expandTimeline(plan, initial = {}, screen) {
  if (!Number.isInteger(plan.frames) || plan.frames < 1) throw invalid('frames must be at least 1.');
  if (plan.frames > MAX_FRAMES) throw new BridgeError('GESTURE_TOO_LONG', `${plan.frames} frames; the limit is ${MAX_FRAMES}.`);
  const normalized = plan.space === 'normalized';
  if (normalized && !screen) throw invalid('A normalized plan needs the window size.');
  const hold = plan.holdKeys ?? [];
  const held = [...(initial.keys ?? [])];
  let x = initial.x ?? Number.NaN;
  let y = initial.y ?? Number.NaN;
  let buttons = initial.buttons ?? 0;
  let previousButtons = buttons;
  let previousKeys = [...held];
  const rows = [];
  let next = 0;
  for (let f = 0; f < plan.frames; f += 1) {
    let wheel = 0;
    while (next < plan.steps.length && plan.steps[next].f <= f) {
      const step = plan.steps[next];
      if (step.f < f) throw invalid('steps must be sorted by f.');
      if (step.x !== undefined || step.y !== undefined) {
        x = normalized ? step.x * screen.width : step.x;
        y = normalized ? step.y * screen.height : step.y;
      }
      buttons = (buttons | (step.set ?? 0)) & ~(step.clear ?? 0);
      for (const key of step.keysAdd ?? []) if (!held.includes(key)) held.push(key);
      for (const key of step.keysDel ?? []) { const at = held.indexOf(key); if (at >= 0) held.splice(at, 1); }
      wheel += step.wheel ?? 0;
      next += 1;
    }
    const keys = [...new Set([...held, ...hold])];
    rows.push({
      x, y, buttons, down: buttons & ~previousButtons, up: previousButtons & ~buttons, wheel,
      keys, keysDown: keys.filter((key) => !previousKeys.includes(key)), keysUp: previousKeys.filter((key) => !keys.includes(key)),
    });
    previousButtons = buttons;
    previousKeys = keys;
  }
  if (next < plan.steps.length) throw invalid('A step lies beyond the last frame.');
  // Frames before the first known position start where the pointer first lands.
  const first = rows.findIndex((row) => !Number.isNaN(row.x) && !Number.isNaN(row.y));
  for (let f = 0; f < first; f += 1) { rows[f].x = rows[first].x; rows[f].y = rows[first].y; }
  return { rows, post: { x, y, buttons, keys: [...held] } };
}

// Picks the element warudo_ui_click presses from ui.list rows. A disabled, hidden or off-screen match never wins
// over a usable one, and several usable matches need an index so the click is never a guess.
// path (case-insensitive substring of the element path) names a control that has no text, such as an icon button.
export function resolveUiTarget(items, { id, label, path, labelMatch = 'contains', index } = {}, { truncated = false } = {}) {
  if ([id, label, path].filter((value) => value !== undefined).length !== 1) throw invalid('Pass exactly one of id, label or path.');
  if (path !== undefined && (typeof path !== 'string' || !path.trim())) throw invalid('path must be a non-empty string.');
  if (labelMatch !== 'exact' && labelMatch !== 'contains' && labelMatch !== 'auto') throw invalid('labelMatch must be exact, contains or auto.');
  if (label !== undefined && (typeof label !== 'string' || !label.trim())) throw invalid('label must be a non-empty string.');
  if (index !== undefined && (!Number.isInteger(index) || index < 0)) throw invalid('index must be an integer >= 0.');
  const wanted = label !== undefined ? label.trim().toLowerCase() : null;
  const wantedPath = path !== undefined ? path.trim().toLowerCase() : null;
  const textOf = (item) => String(item.label ?? '').trim().toLowerCase();
  const isUsable = (item) => item.interactable && item.onScreen;
  let matches;
  if (id !== undefined) matches = items.filter((item) => item.id === id);
  else if (path !== undefined) matches = items.filter((item) => String(item.path ?? '').toLowerCase().includes(wantedPath));
  else if (labelMatch === 'exact') matches = items.filter((item) => textOf(item) === wanted);
  else if (labelMatch === 'contains') matches = items.filter((item) => textOf(item).includes(wanted));
  else {
    // auto: an exact label wins as soon as one usable control has it; otherwise any control whose label contains the text.
    const exact = items.filter((item) => textOf(item) === wanted);
    matches = exact.some(isUsable) ? exact : items.filter((item) => textOf(item).includes(wanted));
  }
  const what = id !== undefined ? `id ${id}` : path !== undefined ? `path ~ "${path}"` : `label ${labelMatch === 'exact' ? '=' : labelMatch === 'auto' ? '=~' : '~'} "${label}"`;
  const brief = (item) => ({ id: item.id, path: item.path, label: item.label, interactable: item.interactable, onScreen: item.onScreen, rect: item.rect });
  if (!matches.length) {
    throw new BridgeError('UI_NOT_FOUND', `No visible UI element with ${what}.${truncated ? ' The list was truncated; narrow it with ui.list query.' : ''} Read warudo_ui_list to see what is on screen.`);
  }
  const usable = matches.filter(isUsable);
  if (!usable.length) {
    throw new BridgeError('UI_NOT_CLICKABLE', `${matches.length} element(s) match ${what}, but none can be clicked (disabled, or off screen).`, { candidates: matches.slice(0, 10).map(brief) });
  }
  if (index !== undefined) {
    if (index >= usable.length) throw invalid(`index ${index} is out of range: ${usable.length} usable element(s) match ${what}.`);
    return usable[index];
  }
  if (usable.length > 1) {
    throw new BridgeError('UI_AMBIGUOUS', `${usable.length} elements match ${what}; pass index (0-based, in warudo_ui_list order) or the id of one.`, { candidates: usable.slice(0, 10).map(brief) });
  }
  return usable[0];
}

// Where to click an element: the middle of its rectangle, clipped to the window.
export function clickPoint(item, screen) {
  const [x, y, width, height] = item.rect;
  const left = screen ? Math.max(x, 0) : x;
  const top = screen ? Math.max(y, 0) : y;
  const right = screen ? Math.min(x + width, screen.width) : x + width;
  const bottom = screen ? Math.min(y + height, screen.height) : y + height;
  return [round3((left + right) / 2), round3((top + bottom) / 2)];
}
