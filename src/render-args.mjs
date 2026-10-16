import { BridgeError } from './transport.mjs';
import { SHOT_BACKGROUNDS, SHOT_FRAMINGS, SHOT_VIEWS } from './tools.mjs';

// Argument checks for the render tools that the JSON schema alone does not enforce (the MCP server does not
// validate input against it). The runtime checks again; this answers before a request is queued.
const invalid = (message) => new BridgeError('INVALID_ARGUMENT', message);

function number(args, key, min, max, { integer = false } = {}) {
  const value = args[key];
  if (value === undefined) return;
  if (typeof value !== 'number' || !Number.isFinite(value) || (integer && !Number.isInteger(value))) throw invalid(`${key} must be a ${integer ? 'whole ' : ''}number.`);
  if (value < min || value > max) throw invalid(`${key} must be ${min}..${max}.`);
}

function oneOf(args, key, allowed) {
  if (args[key] !== undefined && !allowed.includes(args[key])) throw invalid(`${key} must be one of ${allowed.join(', ')}.`);
}

export function validateCharacterShotArgs(args) {
  oneOf(args, 'framing', SHOT_FRAMINGS);
  oneOf(args, 'background', SHOT_BACKGROUNDS);
  number(args, 'size', 256, 2048, { integer: true });
  number(args, 'pitch', -80, 89);
  number(args, 'fov', 10, 120);
  number(args, 'fit', 0.3, 1);
  if (args.yaw !== undefined && (typeof args.yaw !== 'number' || !Number.isFinite(args.yaw))) throw invalid('yaw must be a finite number of degrees.');
  if (args.views !== undefined) {
    if (!Array.isArray(args.views) || args.views.length < 1 || args.views.length > 6) throw invalid(`views takes 1..6 of ${SHOT_VIEWS.join(', ')}.`);
    for (const view of args.views) if (!SHOT_VIEWS.includes(view)) throw invalid(`Unknown view '${view}'. Use ${SHOT_VIEWS.join(', ')}.`);
    if (new Set(args.views).size !== args.views.length) throw invalid('views must not repeat a view.');
    if (args.yaw !== undefined) throw invalid('Pass views or yaw, not both.');
  }
}

const VALIDATORS = { warudo_character_shot: validateCharacterShotArgs };

export function validateRenderArgs(toolName, args) {
  const validate = VALIDATORS[toolName];
  if (validate) validate(args);
}
