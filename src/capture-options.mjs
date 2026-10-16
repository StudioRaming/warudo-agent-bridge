import { BridgeError } from './transport.mjs';

// Argument checks for the image tools (screen_capture, ui_capture, screen_diff, screen_probe, screen_sequence) and for the
// capture options of the pointer gestures. Pure functions: a bad call is refused here, before it reaches WARUDO.
// The runtime checks the same limits again against the live window (REGION_OUTSIDE_SCREEN), because only it knows the window.

export const MAX_SIZE = { min: 256, max: 4096, default: 1600 };
export const GRID_STEP = { min: 10, max: 1000, default: 100 };
export const PADDING = { min: 0, max: 400, default: 16 };
export const SEQUENCE = { frames: { min: 2, max: 16, default: 6 }, every: { min: 1, max: 120, default: 10 }, maxSpan: 1200 };
export const DIFF = { threshold: { min: 0, max: 255, default: 6 }, mergeGap: { min: 0, max: 64, default: 8 } };
export const PROBE = { points: 64, regions: 32, radius: { min: 0, max: 32, default: 2 } };
export const UI_CAPTURE_LIMIT = { min: 1, max: 200, default: 60 };

// The options every image tool and every capturing gesture shares.
export const CAPTURE_KEYS = ['maxSize', 'grid', 'gridStep', 'region', 'around', 'padding', 'markPointer', 'captureBefore'];

const invalid = (message) => new BridgeError('INVALID_ARGUMENT', message);

function integer(value, min, max, name) {
  if (!Number.isInteger(value) || value < min || value > max) throw invalid(`${name} must be an integer ${min}..${max}.`);
  return value;
}

function bool(value, name) {
  if (typeof value !== 'boolean') throw invalid(`${name} must be true or false.`);
  return value;
}

// True when a call passes any capture option, so an older runtime that would silently ignore them is refused instead.
export function captureOptionsUsed(args = {}) {
  return CAPTURE_KEYS.some((key) => args[key] !== undefined);
}

// [x, y, width, height] in window pixels. With the window size known the region must lie inside it.
export function validateRegion(region, screen, name = 'region') {
  if (!Array.isArray(region) || region.length !== 4 || !region.every((value) => typeof value === 'number' && Number.isFinite(value))) {
    throw invalid(`${name} must be [x, y, width, height] with finite numbers.`);
  }
  const [x, y, width, height] = region;
  if (width <= 0 || height <= 0) throw invalid(`${name} width and height must be positive.`);
  if (screen && (x < 0 || y < 0 || x + width > screen.width + 1e-6 || y + height > screen.height + 1e-6)) {
    throw new BridgeError('REGION_OUTSIDE_SCREEN', `${name} [${region.join(', ')}] is not inside the ${screen.width}x${screen.height} window.`);
  }
  return region;
}

function validateAround(around) {
  if (around === null || typeof around !== 'object' || Array.isArray(around)) throw invalid('around must be an object with objectId or path.');
  const keys = Object.keys(around);
  if (keys.some((key) => key !== 'objectId' && key !== 'path')) throw invalid('around takes objectId or path only.');
  const hasId = around.objectId !== undefined;
  const hasPath = around.path !== undefined;
  if (hasId === hasPath) throw invalid('around needs exactly one of objectId or path.');
  if (hasId && !Number.isInteger(around.objectId)) throw invalid('around.objectId must be an integer.');
  if (hasPath && (typeof around.path !== 'string' || !around.path)) throw invalid('around.path must be a non-empty string.');
  return around;
}

// Returns only the options that were passed, checked. screen ({ width, height }) is optional.
export function normalizeCaptureOptions(args = {}, { screen } = {}) {
  const options = {};
  if (args.maxSize !== undefined) options.maxSize = integer(args.maxSize, MAX_SIZE.min, MAX_SIZE.max, 'maxSize');
  if (args.grid !== undefined) options.grid = bool(args.grid, 'grid');
  if (args.gridStep !== undefined) options.gridStep = integer(args.gridStep, GRID_STEP.min, GRID_STEP.max, 'gridStep');
  if (args.padding !== undefined) options.padding = integer(args.padding, PADDING.min, PADDING.max, 'padding');
  if (args.markPointer !== undefined) options.markPointer = bool(args.markPointer, 'markPointer');
  if (args.captureBefore !== undefined) options.captureBefore = bool(args.captureBefore, 'captureBefore');
  if (args.region !== undefined && args.around !== undefined) throw invalid('Pass region or around, not both.');
  if (args.region !== undefined) options.region = validateRegion(args.region, screen);
  if (args.around !== undefined) options.around = validateAround(args.around);
  if (options.gridStep !== undefined && options.grid === undefined) options.grid = true;
  return options;
}

function validateScreenCapture(args, context) {
  const options = normalizeCaptureOptions(args, context);
  if (options.markPointer !== undefined || options.captureBefore !== undefined) throw invalid('markPointer and captureBefore belong to the pointer tools.');
  const result = { ...options };
  if (args.timeoutMs !== undefined) result.timeoutMs = integer(args.timeoutMs, 100, 10000, 'timeoutMs');
  return result;
}

function validateUiCapture(args, context) {
  const result = validateScreenCapture(args, context);
  if (args.query !== undefined) {
    if (typeof args.query !== 'string') throw invalid('query must be a string.');
    result.query = args.query;
  }
  if (args.interactableOnly !== undefined) result.interactableOnly = bool(args.interactableOnly, 'interactableOnly');
  if (args.limit !== undefined) result.limit = integer(args.limit, UI_CAPTURE_LIMIT.min, UI_CAPTURE_LIMIT.max, 'limit');
  return result;
}

function validateDiff(args, context) {
  const result = validateScreenCapture(args, context);
  if (typeof args.baseline !== 'string' || !args.baseline.trim()) throw invalid('baseline must be the path of a PNG returned by a capture tool.');
  result.baseline = context.evidencePath ? context.evidencePath(args.baseline) : args.baseline;
  if (args.threshold !== undefined) result.threshold = integer(args.threshold, DIFF.threshold.min, DIFF.threshold.max, 'threshold');
  if (args.mergeGap !== undefined) result.mergeGap = integer(args.mergeGap, DIFF.mergeGap.min, DIFF.mergeGap.max, 'mergeGap');
  return result;
}

function validateSequence(args, context) {
  const result = validateScreenCapture(args, context);
  const frames = args.frames === undefined ? SEQUENCE.frames.default : integer(args.frames, SEQUENCE.frames.min, SEQUENCE.frames.max, 'frames');
  const every = args.everyFrames === undefined ? SEQUENCE.every.default : integer(args.everyFrames, SEQUENCE.every.min, SEQUENCE.every.max, 'everyFrames');
  if ((frames - 1) * every > SEQUENCE.maxSpan) {
    throw new BridgeError('SEQUENCE_TOO_LONG', `${frames} frames every ${every} frames span ${(frames - 1) * every} rendered frames; the limit is ${SEQUENCE.maxSpan}. Use fewer frames or a smaller everyFrames.`);
  }
  if (args.frames !== undefined) result.frames = frames;
  if (args.everyFrames !== undefined) result.everyFrames = every;
  return result;
}

function validateProbe(args, context) {
  const { points, regions } = args;
  if (points === undefined && regions === undefined) throw invalid('Pass points, regions or both.');
  const result = {};
  if (points !== undefined) {
    if (!Array.isArray(points) || points.length < 1 || points.length > PROBE.points) throw invalid(`points needs 1..${PROBE.points} entries of [x, y].`);
    result.points = points.map((point, index) => {
      if (!Array.isArray(point) || point.length !== 2 || !point.every((value) => typeof value === 'number' && Number.isFinite(value))) throw invalid(`points[${index}] must be [x, y].`);
      if (context.screen && (point[0] < 0 || point[1] < 0 || point[0] >= context.screen.width || point[1] >= context.screen.height)) {
        throw new BridgeError('REGION_OUTSIDE_SCREEN', `points[${index}] [${point.join(', ')}] is not inside the ${context.screen.width}x${context.screen.height} window.`);
      }
      return point;
    });
  }
  if (regions !== undefined) {
    if (!Array.isArray(regions) || regions.length < 1 || regions.length > PROBE.regions) throw invalid(`regions needs 1..${PROBE.regions} entries of [x, y, width, height].`);
    result.regions = regions.map((region, index) => validateRegion(region, context.screen, `regions[${index}]`));
  }
  if (args.radius !== undefined) result.radius = integer(args.radius, PROBE.radius.min, PROBE.radius.max, 'radius');
  if (args.timeoutMs !== undefined) result.timeoutMs = integer(args.timeoutMs, 100, 10000, 'timeoutMs');
  return result;
}

// Checked and cleaned arguments for an image tool, keyed by the tool's `check` in tools.mjs. Unknown keys are not passed on.
export const CHECKS = {
  screen_capture: validateScreenCapture,
  ui_capture: validateUiCapture,
  screen_diff: validateDiff,
  screen_sequence: validateSequence,
  screen_probe: validateProbe,
};

export function validateCaptureArgs(check, args = {}, context = {}) {
  const run = CHECKS[check];
  if (!run) throw new Error(`Unknown capture check "${check}".`);
  return run(args, context);
}
