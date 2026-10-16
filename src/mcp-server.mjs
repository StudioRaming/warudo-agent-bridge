import readline from 'node:readline';
import process from 'node:process';
import { request, responseError, readEvidencePng, readHeartbeat, evidencePngPath, defaultOwner, BridgeError, BridgeUnknownError } from './transport.mjs';
import { captureOptionsUsed, validateCaptureArgs, CAPTURE_KEYS } from './capture-options.mjs';
import { ImageStore } from './image-space.mjs';
import { legendFrom, listParamsFor, resolveMark } from './ui-marks.mjs';
import { buildClickPlan, buildPointerPlan, clickPoint, resolveUiTarget } from './pointer-timeline.mjs';
import { TOOLS, TOOL_BY_NAME } from './tools.mjs';
import { VERSION } from './version.mjs';

// Dual-era MCP server over stdio (https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning).
// Modern clients send the protocol version in every request's _meta; legacy
// clients open with an initialize handshake.
export const MODERN_VERSIONS = ['2026-07-28'];
export const LEGACY_VERSIONS = ['2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05'];
const VERSION_META = 'io.modelcontextprotocol/protocolVersion';
const SERVER_INFO = { name: 'warudo-agent-bridge', title: 'WARUDO Agent Bridge', version: VERSION };

export const INSTRUCTIONS = [
  'Controls a running WARUDO (VTuber software) on this PC through a local file mailbox.',
  'Start with warudo_status. Read before you write: asset ids, field names, blueprint and node ids all come from list/get tools.',
  'Writes need a short lease; this server takes it automatically when no other agent holds it. Release it with warudo_lease_release when you are done.',
  'Live changes are not saved into the scene file until warudo_scene_save_as. Mutating tools return before/after values so you can restore them.',
  'An UNKNOWN error means the request may have run: read the state back instead of retrying. NOT_EXECUTED means it never ran and is safe to retry.',
  'To pose a character from a reference image: warudo_character_pose_get, then warudo_character_pose_set with muscles, capture with warudo_camera_capture, compare, adjust; save with warudo_character_pose_save.',
].join(' ');

const textContent = (value) => ({ type: 'text', text: typeof value === 'string' ? value : JSON.stringify(value) });
const CAPTURE_TOOLS = new Set(['warudo_camera_capture', 'warudo_screen_capture', 'warudo_analysis_capture', 'warudo_ui_capture']);
const isCapture = (name) => CAPTURE_TOOLS.has(name);
// Capture tools return the PNG path at the top; a pointer gesture with capture=true returns it under "capture".
const evidencePath = (name, data) => (isCapture(name) ? data && data.path : data && data.capture && data.capture.path);
// The capture options a gesture forwards from the arguments it was called with.
const captureArgs = (args) => Object.fromEntries(CAPTURE_KEYS.filter((key) => args[key] !== undefined).map((key) => [key, args[key]]));
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const IDEMPOTENT_KINDS = new Set(['read', 'set', 'lease']);
// 2026-07-28 list results must carry ttlMs (int >= 0) and cacheScope ("public" | "private"); Claude Code rejects them otherwise.
// Values equal the SDK defaults: no caching, private scope. The tool list is fixed per process (listChanged: false).
const LIST_CACHE_FIELDS = { ttlMs: 0, cacheScope: 'private' };

function errorPayload(error) {
  if (error instanceof BridgeUnknownError) return { code: 'UNKNOWN', message: error.message, retrySafe: false, details: error.details };
  if (error instanceof BridgeError) {
    return { code: error.code, message: error.message, retrySafe: error.code === 'NOT_EXECUTED' || error.code === 'BRIDGE_UNAVAILABLE', ...(error.details && !error.details.response ? { details: error.details } : {}) };
  }
  return { code: 'INTERNAL', message: error && error.message ? error.message : String(error), retrySafe: false };
}

export function createServer({ home, owner = defaultOwner('mcp'), timeoutMs, autoLease = true } = {}) {
  const transportOptions = { home, owner, timeoutMs };
  let legacyVersion = null;
  // The last 32 images this server returned, with where each sits in the window (src/image-space.mjs).
  const images = new ImageStore();
  // The numbered legend of the latest warudo_ui_capture, for warudo_ui_click mark=N.
  let uiLegend = null;

  // idempotent: reads and absolute-value writes may be retried once if a reload interrupts them.
  async function operation(name, args, idempotent = false) {
    const response = await request(name, args, { ...transportOptions, idempotent });
    const failed = responseError(response);
    if (failed) throw failed;
    return response.data;
  }

  // The runtime checks the lease before it runs anything, so a LEASE_REQUIRED
  // reply proves the first attempt did nothing and one retry is safe.
  async function callRuntime(tool, args) {
    try {
      return await operation(tool.operation, args, IDEMPOTENT_KINDS.has(tool.kind));
    } catch (error) {
      if (!(autoLease && tool.needsLease && error instanceof BridgeError && error.code === 'LEASE_REQUIRED')) throw error;
    }
    await operation('lease.acquire', { seconds: 120 }, true);
    return operation(tool.operation, args, IDEMPOTENT_KINDS.has(tool.kind));
  }

  // A newer MCP server can run against the runtime that is still installed in WARUDO. Say so, instead of
  // letting the runtime answer UNKNOWN_OPERATION for every call of a tool it does not have.
  // A feature found once stays found for that runtime session; a reload gets a new sessionId and is checked again.
  // The session id comes from the heartbeat file (no mailbox round trip); a miss falls back to the status operation,
  // which also waits out a reloading bridge. A missing feature is never cached, so a reinstall is noticed at once.
  const confirmedFeatures = new Map();
  async function requireFeature(feature) {
    let sessionId;
    try {
      sessionId = (await readHeartbeat(home, { allowStale: true })).state.sessionId;
    } catch {
      sessionId = undefined;
    }
    if (sessionId && confirmedFeatures.get(sessionId)?.has(feature)) return;
    const state = await operation('status', {}, true);
    if (Array.isArray(state.features) && state.features.includes(feature)) {
      sessionId = state.sessionId ?? sessionId;
      if (sessionId) confirmedFeatures.set(sessionId, new Set([...(confirmedFeatures.get(sessionId) ?? []), feature]));
      return;
    }
    throw new BridgeError('RUNTIME_OUTDATED', `The WARUDO runtime (version ${state.version ?? 'unknown'}) has no "${feature}" feature. Reinstall the bridge and let WARUDO recompile Playground (about 30 seconds): npx @studioraming/warudo-agent-bridge install`);
  }

  // Finds the control with ui.list, then clicks the middle of its rectangle through the same pointer timeline.
  // expectObject makes the runtime stop with COVERED instead of clicking whatever is on top of it.
  async function uiClick(tool, args) {
    const { id, label, near, mark, labelMatch, index } = args;
    if ([id, label, near, mark].filter((value) => value !== undefined).length !== 1) throw new BridgeError('INVALID_ARGUMENT', 'Pass exactly one of near, label, id or mark.');
    let target;
    let screen;
    if (mark !== undefined) {
      // The numbers belong to the latest ui_capture; list the same controls again and refuse when they are not the same.
      const listed = uiLegend ? await operation('ui.list', listParamsFor(uiLegend), true) : null;
      target = resolveMark(uiLegend, mark, listed);
      if (!target.interactable || !target.onScreen) throw new BridgeError('UI_NOT_CLICKABLE', `Mark ${mark} (${target.label ?? target.path}) is disabled or off screen.`, { candidates: [{ id: target.id, path: target.path, label: target.label, interactable: target.interactable, onScreen: target.onScreen, rect: target.rect }] });
      screen = listed.screen;
    } else {
      // near is the preferred way: the visible text, exact first, then contains.
      const text = near ?? label;
      const wanted = id === undefined ? { label: text, labelMatch: labelMatch ?? (near !== undefined ? 'auto' : 'contains') } : { id };
      const listed = await operation('ui.list', { ...(id !== undefined ? { id } : { query: text }), interactableOnly: false, limit: 500 }, true);
      target = resolveUiTarget(listed.items ?? [], { ...wanted, index }, { truncated: listed.truncated === true });
      screen = listed.screen;
    }
    const [x, y] = clickPoint(target, screen);
    const plan = buildClickPlan({ x, y, button: args.button, count: args.count, modifiers: args.modifiers, holdFrames: args.holdFrames, settleFrames: args.settleFrames, capture: args.capture, ...captureArgs(args), expectObject: target.id });
    const result = await callRuntime(tool, plan);
    return { ...result, resolved: { id: target.id, path: target.path, label: target.label, point: [x, y] } };
  }

  // Remembers where an image sits in the window and tells the caller its imageId (top level of a capture tool's reply, under
  // "capture" for a gesture).
  function registerImage(name, data) {
    const holder = isCapture(name) ? data : data && data.capture;
    const mapping = holder && holder.mapping;
    if (!mapping || typeof mapping !== 'object') return;
    try {
      holder.imageId = images.add(mapping);
    } catch {
      // A mapping this server cannot read (an older runtime): the image is still returned, just without an imageId.
    }
  }

  async function waitLoaded(args) {
    const timeout = Math.min(Math.max(Number(args.timeoutMs) || 30_000, 500), 120_000);
    const started = Date.now();
    let asset = null;
    while (Date.now() - started < timeout) {
      asset = await operation('asset.get', { assetId: args.assetId }, true);
      const loaded = asset && asset.active === true && asset.gameObjectId != null && (!args.source || asset.source === args.source);
      if (loaded) return { loaded: true, waitedMs: Date.now() - started, asset };
      await sleep(250);
    }
    return { loaded: false, waitedMs: Date.now() - started, asset, hint: 'Still loading, or the source failed. Check warudo_runtime_logs.' };
  }

  async function callTool(name, args, modern) {
    const tool = TOOL_BY_NAME.get(name);
    let data;
    try {
      // space="image" first: x/y, path, points and region become window pixels, then the argument checks run on those.
      let callArgs = tool.inputSchema.properties.space?.enum?.includes('image') ? images.convert(args).args : args;
      if (tool.check) callArgs = validateCaptureArgs(tool.check, callArgs, { evidencePath: (candidate) => evidencePngPath(home, candidate) });
      if (tool.requires) await requireFeature(tool.requires);
      if (tool.requiresCapture && captureOptionsUsed(callArgs)) await requireFeature('capture-tools');
      if (tool.local === 'waitLoaded') data = await waitLoaded(callArgs);
      else if (tool.local === 'uiClick') data = await uiClick(tool, callArgs);
      else data = await callRuntime(tool, tool.plan ? buildPointerPlan(tool.plan, callArgs) : callArgs);
      registerImage(name, data);
      if (name === 'warudo_ui_capture') uiLegend = legendFrom(data) ?? uiLegend;
    } catch (error) {
      return { isError: true, content: [textContent(errorPayload(error))] };
    }
    const content = [textContent(data)];
    const capturePath = evidencePath(name, data);
    if (capturePath) {
      try {
        const image = await readEvidencePng(home, capturePath);
        content.push({ type: 'image', data: image.data.toString('base64'), mimeType: 'image/png' });
      } catch (error) {
        return { isError: true, content: [textContent(errorPayload(error))] };
      }
    }
    const result = { content, isError: false };
    const structured = modern || (legacyVersion && legacyVersion >= '2025-06-18');
    if (structured && data !== undefined && (modern || (data && typeof data === 'object' && !Array.isArray(data)))) result.structuredContent = data;
    return result;
  }

  const listTools = () => TOOLS.map(({ name, title, description, inputSchema, annotations }) => ({ name, title, description, inputSchema, annotations }));

  // Returns a JSON-RPC response object, or null for notifications.
  async function handle(message) {
    if (!message || typeof message !== 'object' || message.jsonrpc !== '2.0' || typeof message.method !== 'string') {
      return rpcError(message && message.id, -32600, 'Invalid JSON-RPC request.');
    }
    const { id, method } = message;
    const params = message.params && typeof message.params === 'object' ? message.params : {};
    if (id === undefined || id === null) return null; // Notifications never get a reply.

    const requested = params._meta && params._meta[VERSION_META];
    const modern = requested !== undefined;
    if (modern && !MODERN_VERSIONS.includes(requested)) {
      return rpcError(id, -32022, 'Unsupported protocol version', { supported: [...MODERN_VERSIONS, ...LEGACY_VERSIONS], requested });
    }
    const ok = (result) => ({ jsonrpc: '2.0', id, result: modern ? { resultType: 'complete', ...result } : result });
    const list = (result) => (modern ? { ...LIST_CACHE_FIELDS, ...result } : result);

    switch (method) {
      case 'initialize': {
        const asked = params.protocolVersion;
        legacyVersion = LEGACY_VERSIONS.includes(asked) ? asked : LEGACY_VERSIONS[0];
        return ok({ protocolVersion: legacyVersion, capabilities: { tools: { listChanged: false } }, serverInfo: SERVER_INFO, instructions: INSTRUCTIONS });
      }
      case 'server/discover':
        return ok(list({
          supportedVersions: [...MODERN_VERSIONS, ...LEGACY_VERSIONS],
          capabilities: { tools: { listChanged: false } },
          _meta: { 'io.modelcontextprotocol/serverInfo': SERVER_INFO },
          instructions: INSTRUCTIONS,
        }));
      case 'ping':
        return ok({});
      case 'tools/list':
        return ok(list({ tools: listTools() }));
      case 'tools/call': {
        if (typeof params.name !== 'string') return rpcError(id, -32602, 'tools/call requires params.name.');
        if (!TOOL_BY_NAME.has(params.name)) return rpcError(id, -32602, `Unknown tool: ${params.name}`);
        const args = params.arguments && typeof params.arguments === 'object' && !Array.isArray(params.arguments) ? params.arguments : {};
        return ok(await callTool(params.name, args, modern));
      }
      case 'resources/list':
        return ok(list({ resources: [] }));
      case 'resources/templates/list':
        return ok(list({ resourceTemplates: [] }));
      case 'prompts/list':
        return ok(list({ prompts: [] }));
      default:
        return rpcError(id, -32601, `Method not found: ${method}`);
    }
  }

  return { handle, listTools };
}

function rpcError(id, code, message, data) {
  return { jsonrpc: '2.0', id: id ?? null, error: { code, message, ...(data === undefined ? {} : { data }) } };
}

export function runStdioServer(options = {}) {
  const server = createServer(options);
  const input = options.input || process.stdin;
  const output = options.output || process.stdout;
  const write = (message) => output.write(`${JSON.stringify(message)}\n`);
  const pending = new Set();

  const lines = readline.createInterface({ input, crlfDelay: Infinity });
  lines.on('line', (line) => {
    if (!line.trim()) return;
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      write(rpcError(null, -32700, 'Parse error.'));
      return;
    }
    const task = (async () => {
      try {
        if (Array.isArray(message)) {
          const replies = (await Promise.all(message.map((entry) => server.handle(entry)))).filter(Boolean);
          if (replies.length) write(replies);
        } else {
          const reply = await server.handle(message);
          if (reply) write(reply);
        }
      } catch (error) {
        process.stderr.write(`[warudo-agent-bridge] ${error && error.stack ? error.stack : error}\n`);
        if (!Array.isArray(message) && message && message.id !== undefined && message.id !== null) write(rpcError(message.id, -32603, 'Internal error.'));
      }
    })();
    pending.add(task);
    task.finally(() => pending.delete(task));
  });
  lines.on('close', async () => {
    await Promise.allSettled([...pending]);
    if (!options.keepAlive) process.exit(0);
  });
  return server;
}
