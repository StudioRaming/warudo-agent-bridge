import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { bridgeHome, runtimeDir } from './paths.mjs';

// Protocol 1: WARUDO reads requests/<id>.json in place.
// Protocol 2: WARUDO first renames requests/<id>.json to <id>.claimed. A client
// that times out can then withdraw an unclaimed request and know it never ran.
export const CLIENT_PROTOCOLS = [1, 2];
export const MAX_REQUEST_BYTES = 64 * 1024;
export const DEFAULT_REQUEST_TIMEOUT_MS = 65_000;
// A heartbeat older than this (while WARUDO runs) means the bridge is not serving right now.
export const HEARTBEAT_STALE_MS = Number(process.env.WARUDO_AGENT_BRIDGE_STALE_MS || 5_000);
// How long a request waits for a reloading or busy bridge to come back before failing.
export const RECONNECT_MS = Number(process.env.WARUDO_AGENT_BRIDGE_RECONNECT_MS || 30_000);
const WATCH_INTERVAL_MS = 1_000;

export class BridgeError extends Error {
  constructor(code, message, details = {}) {
    super(message);
    this.name = 'BridgeError';
    this.code = code;
    this.details = details;
  }
}

// The request may or may not have run. Never retry a mutation automatically.
export class BridgeUnknownError extends BridgeError {
  constructor(message, details = {}) {
    super('UNKNOWN', message, details);
    this.name = 'BridgeUnknownError';
  }
}

export function defaultOwner(prefix = 'agent') {
  return `${prefix}-${process.pid}-${crypto.randomUUID().slice(0, 8)}`.slice(0, 128);
}

function requiredOwner(owner) {
  const value = String(owner || '');
  if (!value.trim() || value.length > 128) throw new BridgeError('INVALID_OWNER', 'owner must be 1..128 characters.');
  return value;
}

function processAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === 'EPERM';
  }
}

// Options accept `home` (new) or `root` (0.1 name for the same directory).
const homeOf = (options = {}) => options.home ?? options.root;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const TRANSIENT_FS = new Set(['ENOENT', 'EBUSY', 'EPERM', 'EACCES']);

// The runtime replaces state.json twice a second (File.Replace). A read that lands inside the
// swap can see no file, a sharing violation, or a cut-off body; read again before believing it.
async function readState(file) {
  let failure;
  for (let attempt = 0; attempt < 5; attempt += 1) {
    if (attempt) await sleep(30);
    try {
      return JSON.parse(await fs.readFile(file, 'utf8'));
    } catch (error) {
      failure = error;
      if (error.code && !TRANSIENT_FS.has(error.code)) break;
    }
  }
  throw failure;
}

export async function readHeartbeat(explicitHome, { allowStale = false } = {}) {
  const root = runtimeDir(explicitHome);
  let state;
  try {
    state = await readState(path.join(root, 'state.json'));
  } catch (error) {
    if (error instanceof SyntaxError) throw new BridgeError('BRIDGE_UNAVAILABLE', 'runtime/state.json is malformed; wait one second and retry.', { root });
    throw new BridgeError('BRIDGE_UNAVAILABLE', 'WARUDO has never started the bridge on this machine (runtime/state.json is missing). Install the runtime and start WARUDO.', { root, cause: error.code });
  }
  if (!state || typeof state.sessionId !== 'string' || !state.sessionId || !Number.isInteger(state.pid)) {
    throw new BridgeError('BRIDGE_UNAVAILABLE', 'runtime/state.json is missing sessionId or pid.', { root });
  }
  const ageMs = Number.isFinite(state.heartbeatEpoch) ? Date.now() - state.heartbeatEpoch : Infinity;
  const alive = processAlive(state.pid);
  const health = { ageMs, processAlive: alive, stopped: state.stopped === true };
  if (!allowStale) {
    if (health.stopped) throw new BridgeError('BRIDGE_UNAVAILABLE', 'The WARUDO bridge was unloaded (WARUDO closed or the Playground script reloaded). Wait for WARUDO to start it again.', { root, ...health });
    if (!alive) throw new BridgeError('BRIDGE_UNAVAILABLE', `WARUDO is not running (last bridge process ${state.pid} has exited).`, { root, ...health });
    if (ageMs > HEARTBEAT_STALE_MS) throw new BridgeError('BRIDGE_UNAVAILABLE', `The WARUDO bridge heartbeat is ${Math.round(ageMs / 1000)} s old. WARUDO may be frozen, or the bridge failed to compile (check the Playground toast in WARUDO).`, { root, ...health });
  }
  return { root, state, health };
}

function newRequestId(value) {
  const id = value || crypto.randomUUID();
  if (!/^(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})$/i.test(id)) {
    throw new BridgeError('INVALID_REQUEST_ID', 'requestId must be a UUID.');
  }
  return id.replaceAll('-', '').toLowerCase();
}

async function atomicWrite(file, body) {
  const temp = `${file}.${process.pid}.${crypto.randomUUID()}.tmp`;
  await fs.writeFile(temp, body, { encoding: 'utf8', flag: 'wx' });
  await fs.rename(temp, file);
}

async function readResponse(file, requestId) {
  let body;
  try {
    body = await fs.readFile(file, 'utf8');
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    // The runtime replaces the file atomically; a sharing violation is transient.
    if (error.code === 'EBUSY' || error.code === 'EPERM') return null;
    throw new BridgeError('RESPONSE_READ_FAILED', 'Could not read the WARUDO response.', { responsePath: file, cause: error.code });
  }
  let response;
  try {
    response = JSON.parse(body);
  } catch {
    throw new BridgeError('RESPONSE_MALFORMED', 'WARUDO wrote a malformed response.', { responsePath: file });
  }
  if (!response || response.requestId !== requestId) {
    throw new BridgeError('RESPONSE_MALFORMED', 'WARUDO response requestId does not match.', { responsePath: file });
  }
  return response;
}

// Try to take an unclaimed request back. true means WARUDO never saw it.
// ENOENT means the runtime claimed it first. A sharing violation (an antivirus scan, or the
// runtime listing the folder) is momentary, so retry it before giving the request up as claimed.
async function withdraw(requestPath) {
  const withdrawn = `${requestPath}.withdrawn`;
  for (let attempt = 0; ; attempt += 1) {
    try {
      await fs.rename(requestPath, withdrawn);
      break;
    } catch (error) {
      if (error.code === 'ENOENT' || attempt >= 5) return false;
      await sleep(40);
    }
  }
  await fs.rm(withdrawn, { force: true });
  return true;
}

// A Playground reload (script edit, bridge update) or a busy WARUDO leaves the process alive
// with no live bridge for a few seconds. Wait that out instead of failing; fail at once only
// when WARUDO itself is gone.
export async function waitForBridge(home, waitMs = RECONNECT_MS) {
  const deadline = Date.now() + waitMs;
  for (;;) {
    try {
      return await readHeartbeat(home);
    } catch (error) {
      const recovering = error.code === 'BRIDGE_UNAVAILABLE' && error.details && error.details.processAlive === true;
      if (!recovering || Date.now() >= deadline) throw error;
    }
    await sleep(250);
  }
}

// These replies prove the runtime rejected the request before running it.
const REJECTED_BEFORE_RUN = /^(STALE_SESSION|REQUEST_EXPIRED):/;

// Sends one request and, when that is provably safe, one retry after the bridge is back:
// - the request never ran (withdrawn before a reload, or rejected as stale/expired), or
// - options.idempotent is true (a read, or a write of absolute values) and the outcome is unknown
//   because the bridge reloaded mid-run; running it twice gives the same result.
// Anything else that may have run is returned or thrown as is. Passing requestId disables retry.
export async function request(operation, params = {}, options = {}) {
  const retry = options.requestId === undefined && options.retry !== false;
  const idempotent = retry && options.idempotent === true;
  let response;
  try {
    response = await requestOnce(operation, params, options);
  } catch (error) {
    const neverRan = error instanceof BridgeError && error.code === 'NOT_EXECUTED';
    if (!((retry && neverRan) || (idempotent && error instanceof BridgeUnknownError))) throw error;
    return requestOnce(operation, params, options);
  }
  const failure = response.ok === false ? String(response.error || '') : '';
  if ((retry && REJECTED_BEFORE_RUN.test(failure)) || (idempotent && /^INTERRUPTED:/.test(failure))) return requestOnce(operation, params, options);
  return response;
}

async function requestOnce(operation, params = {}, options = {}) {
  const owner = requiredOwner(options.owner || defaultOwner(options.ownerPrefix || 'agent'));
  if (!operation || typeof operation !== 'string' || operation.length > 128) {
    throw new BridgeError('INVALID_OPERATION', 'operation must be a non-empty string of at most 128 characters.');
  }
  if (!params || typeof params !== 'object' || Array.isArray(params)) {
    throw new BridgeError('INVALID_PARAMS', 'params must be an object.');
  }

  const home = homeOf(options);
  const { root, state } = await waitForBridge(home, options.reconnectMs ?? RECONNECT_MS);
  await Promise.all(['requests', 'responses', 'evidence', 'snapshots'].map((name) => fs.mkdir(path.join(root, name), { recursive: true })));
  const protocol = Number(state.protocol) >= 2 ? 2 : 1;
  const requestId = newRequestId(options.requestId);
  const requestPath = path.join(root, 'requests', `${requestId}.json`);
  const responsePath = path.join(root, 'responses', `${requestId}.json`);
  const encoded = JSON.stringify({
    protocol,
    requestId,
    sessionId: state.sessionId,
    createdUtc: new Date().toISOString(),
    owner,
    operation,
    params,
  });
  if (Buffer.byteLength(encoded, 'utf8') > MAX_REQUEST_BYTES) {
    throw new BridgeError('REQUEST_TOO_LARGE', 'request exceeds the 64 KiB mailbox limit.');
  }

  // Reusing a requestId recovers the original reply; a completed request is never submitted twice.
  const existing = await readResponse(responsePath, requestId);
  if (existing) return existing;
  await atomicWrite(requestPath, encoded);

  const timeoutMs = Math.max(1_000, Number(options.timeoutMs || DEFAULT_REQUEST_TIMEOUT_MS));
  const started = Date.now();
  const deadline = started + timeoutMs;
  let nextWatch = started + WATCH_INTERVAL_MS;
  const unknown = (reason) => new BridgeUnknownError(`${reason} The request may or may not have run; do not retry a mutation automatically. Read state back first.`, {
    requestId, requestPath, responsePath, sessionId: state.sessionId,
  });

  while (Date.now() < deadline) {
    const response = await readResponse(responsePath, requestId);
    if (response) return response;
    if (Date.now() >= nextWatch) {
      nextWatch = Date.now() + WATCH_INTERVAL_MS;
      let current = null;
      try {
        current = (await readHeartbeat(home, { allowStale: true })).state;
      } catch { /* a transiently unreadable heartbeat is not a restart */ }
      const restarted = current && (current.sessionId !== state.sessionId || current.stopped === true || !processAlive(current.pid));
      if (restarted) {
        if (protocol >= 2 && await withdraw(requestPath)) {
          throw new BridgeError('NOT_EXECUTED', 'WARUDO stopped or reloaded the bridge before picking up this request. It never ran and is safe to send again after warudo_status succeeds.', { requestId });
        }
        const late = await readResponse(responsePath, requestId);
        if (late) return late;
        throw unknown('WARUDO stopped or reloaded the bridge while this request was pending.');
      }
    }
    await sleep(Date.now() - started < 500 ? 15 : 40);
  }
  if (protocol >= 2 && await withdraw(requestPath)) {
    throw new BridgeError('NOT_EXECUTED', `WARUDO did not pick up the request within ${timeoutMs} ms (it may be busy with a long operation). The request was withdrawn and never ran; it is safe to send again.`, { requestId });
  }
  const late = await readResponse(responsePath, requestId);
  if (late) return late;
  throw unknown(`WARUDO did not answer within ${timeoutMs} ms.`);
}

export function responseError(response) {
  if (!response || response.ok !== false) return null;
  const message = response.error && typeof response.error === 'string' ? response.error : 'WARUDO operation failed.';
  const upper = message.split(':', 1)[0];
  const code = /^[A-Z][A-Z0-9_]+$/.test(upper) ? upper : 'OPERATION_FAILED';
  return new BridgeError(code, message, { response });
}

export function evidencePngPath(explicitHome, candidate) {
  const evidence = path.resolve(path.join(runtimeDir(explicitHome), 'evidence'));
  const resolved = path.resolve(String(candidate || ''));
  const relative = path.relative(evidence, resolved);
  if (!relative || relative.startsWith('..') || path.isAbsolute(relative) || path.extname(resolved).toLowerCase() !== '.png') {
    throw new BridgeError('EVIDENCE_PATH_REJECTED', 'Only PNG files under the bridge evidence directory may be read.', { evidence });
  }
  return resolved;
}

export async function readEvidencePng(explicitHome, candidate) {
  const file = evidencePngPath(explicitHome, candidate);
  let data;
  try {
    // Lexical containment alone permits a PNG symlink or directory junction
    // inside evidence to point outside it. Check the resolved target as well.
    const evidence = await fs.realpath(path.join(runtimeDir(explicitHome), 'evidence'));
    const resolved = await fs.realpath(file);
    const relative = path.relative(evidence, resolved);
    if (!relative || relative === '..' || relative.startsWith('..' + path.sep) ||
        path.isAbsolute(relative) || path.extname(resolved).toLowerCase() !== '.png') {
      throw new BridgeError('EVIDENCE_PATH_REJECTED',
        'Only PNG files resolving inside the bridge evidence directory may be read.', { evidence });
    }
    data = await fs.readFile(resolved);
  } catch (error) {
    if (error instanceof BridgeError) throw error;
    throw new BridgeError('EVIDENCE_READ_FAILED', 'Capture evidence PNG could not be read.', { path: file, cause: error.code });
  }
  return { path: file, data };
}

export { bridgeHome };
