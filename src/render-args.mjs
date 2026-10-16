import { BridgeError } from './transport.mjs';
import { ISOLATE_BACKGROUNDS, ISOLATE_MODES, RENDER_PASSES, RENDER_VIEWS, SHOT_BACKGROUNDS, SHOT_FRAMINGS, SHOT_VIEWS } from './tools.mjs';

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
  number(args, 'aspect', 0.3, 4);
  if (args.yaw !== undefined && (typeof args.yaw !== 'number' || !Number.isFinite(args.yaw))) throw invalid('yaw must be a finite number of degrees.');
  if (args.views !== undefined) {
    if (!Array.isArray(args.views) || args.views.length < 1 || args.views.length > 6) throw invalid(`views takes 1..6 of ${SHOT_VIEWS.join(', ')}.`);
    for (const view of args.views) if (!SHOT_VIEWS.includes(view)) throw invalid(`Unknown view '${view}'. Use ${SHOT_VIEWS.join(', ')}.`);
    if (new Set(args.views).size !== args.views.length) throw invalid('views must not repeat a view.');
    if (args.yaw !== undefined) throw invalid('Pass views or yaw, not both.');
  }
}

const MAX_PASS_PANELS = 12;
const MAX_ISOLATE_PARTS = 16;

function stringList(args, key, max) {
  const list = args[key];
  if (list === undefined) return;
  if (!Array.isArray(list) || list.length > max || list.some((item) => typeof item !== 'string')) throw invalid(`${key} must be a list of at most ${max} strings.`);
}

export function validateRenderPassArgs(args) {
  if (!Array.isArray(args.passes) || args.passes.length < 1) throw invalid(`passes is required: one or more of ${RENDER_PASSES.join(', ')}.`);
  for (const pass of args.passes) if (!RENDER_PASSES.includes(pass)) throw invalid(`Unknown pass '${pass}'. Use ${RENDER_PASSES.join(', ')}.`);
  if (new Set(args.passes).size !== args.passes.length) throw invalid('passes must not repeat a pass.');
  const views = args.views === undefined ? ['camera'] : args.views;
  if (!Array.isArray(views) || views.length < 1 || views.length > 6) throw invalid(`views takes 1..6 of ${RENDER_VIEWS.join(', ')}.`);
  for (const view of views) if (!RENDER_VIEWS.includes(view)) throw invalid(`Unknown view '${view}'. Use ${RENDER_VIEWS.join(', ')}.`);
  if (new Set(views).size !== views.length) throw invalid('views must not repeat a view.');
  if (args.passes.length * views.length > MAX_PASS_PANELS) throw invalid(`passes x views is ${args.passes.length * views.length} panels; the limit is ${MAX_PASS_PANELS}. Ask for fewer passes or views, or call again.`);
  if (args.textureProperty !== undefined && (typeof args.textureProperty !== 'string' || !/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(args.textureProperty))) throw invalid('textureProperty must be a shader property name such as _BaseMap.');
  if (args.passes.includes('texture') && args.textureProperty === undefined) throw invalid('textureProperty is required for the texture pass (for example _BaseMap, _MainTex, _ShadeTex, _EmissionMap).');
  stringList(args, 'include', 40);
  stringList(args, 'exclude', 40);
  if (args.labels !== undefined && typeof args.labels !== 'boolean') throw invalid('labels must be true or false.');
  number(args, 'size', 256, 1024, { integer: true });
  number(args, 'maxSize', 256, 4096, { integer: true });
}

export function validateRenderIsolateArgs(args) {
  const ids = args.objectIds, include = args.include;
  if (ids === undefined && include === undefined) throw invalid('Choose parts with objectIds (from warudo_hierarchy_list) or include (path substrings under the asset).');
  if (ids !== undefined) {
    if (!Array.isArray(ids) || ids.length < 1 || ids.length > MAX_ISOLATE_PARTS || ids.some((id) => !Number.isInteger(id))) throw invalid(`objectIds must be 1..${MAX_ISOLATE_PARTS} whole numbers from warudo_hierarchy_list.`);
    if (new Set(ids).size !== ids.length) throw invalid('objectIds must not repeat an id.');
  }
  if (include !== undefined) {
    stringList(args, 'include', MAX_ISOLATE_PARTS);
    if (include.length < 1 || include.some((item) => item.length === 0)) throw invalid('include takes 1..' + MAX_ISOLATE_PARTS + ' non-empty path substrings.');
  }
  oneOf(args, 'mode', ISOLATE_MODES);
  oneOf(args, 'background', ISOLATE_BACKGROUNDS);
  if (args.background !== undefined && args.mode !== 'only') throw invalid('background only applies to mode only (the default mode is highlight).');
  number(args, 'size', 256, 2048, { integer: true });
  number(args, 'maxSize', 256, 4096, { integer: true });
}

const VALIDATORS = { warudo_character_shot: validateCharacterShotArgs, warudo_render_pass: validateRenderPassArgs, warudo_render_isolate: validateRenderIsolateArgs };

export function validateRenderArgs(toolName, args) {
  const validate = VALIDATORS[toolName];
  if (validate) validate(args);
}
