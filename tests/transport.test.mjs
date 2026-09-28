import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { request, readHeartbeat, readEvidencePng, BridgeError, BridgeUnknownError } from '../src/transport.mjs';
import { startFakeRuntime, deadPid } from './helpers/fake-runtime.mjs';

const listRequests = async (root) => (await fs.readdir(path.join(root, 'requests'))).filter((name) => !name.endsWith('.tmp'));

test('protocol 2 request is claimed, answered, and replayable by requestId', async () => {
  const runtime = await startFakeRuntime({ handler: (op, params) => ({ op, echo: params.value }) });
  try {
    const reply = await request('scene.get', { value: 7 }, { home: runtime.home, owner: 'tester', timeoutMs: 3000 });
    assert.equal(reply.ok, true);
    assert.deepEqual(reply.data, { op: 'scene.get', echo: 7 });
    assert.equal(runtime.seen[0].protocol, 2);
    assert.equal(runtime.seen[0].owner, 'tester');
    const again = await request('scene.get', { value: 7 }, { home: runtime.home, owner: 'tester', requestId: reply.requestId, timeoutMs: 1000 });
    assert.deepEqual(again, reply);
    assert.equal(runtime.seen.length, 1, 'a replayed requestId must not be submitted twice');
  } finally {
    await runtime.stop();
  }
});

test('a protocol 1 runtime receives protocol 1 requests', async () => {
  const runtime = await startFakeRuntime({ protocol: 1 });
  try {
    await request('status', {}, { home: runtime.home, owner: 'tester', timeoutMs: 3000 });
    assert.equal(runtime.seen[0].protocol, 1);
  } finally {
    await runtime.stop();
  }
});

test('oversized requests are refused before touching the mailbox', async () => {
  const runtime = await startFakeRuntime();
  try {
    await assert.rejects(request('status', { big: 'x'.repeat(70 * 1024) }, { home: runtime.home, owner: 'tester' }), (e) => e.code === 'REQUEST_TOO_LARGE');
    assert.deepEqual(await listRequests(runtime.root), []);
  } finally {
    await runtime.stop();
  }
});

test('a dead WARUDO process fails fast instead of waiting for the timeout', async () => {
  const runtime = await startFakeRuntime();
  runtime.freeze();
  await runtime.writeState({ pid: await deadPid() });
  const started = Date.now();
  await assert.rejects(request('status', {}, { home: runtime.home, owner: 'tester', timeoutMs: 20_000 }), (e) => e instanceof BridgeError && e.code === 'BRIDGE_UNAVAILABLE' && /not running/.test(e.message));
  assert.ok(Date.now() - started < 1000);
  await runtime.stop();
});

test('a bridge that stays stale or unloaded is reported once the reconnect wait runs out', async () => {
  const runtime = await startFakeRuntime();
  runtime.freeze();
  const state = JSON.parse(await fs.readFile(path.join(runtime.root, 'state.json'), 'utf8'));
  await fs.writeFile(path.join(runtime.root, 'state.json'), JSON.stringify({ ...state, heartbeatEpoch: Date.now() - 120_000 }));
  await assert.rejects(request('status', {}, { home: runtime.home, owner: 'tester', reconnectMs: 300 }), (e) => e.code === 'BRIDGE_UNAVAILABLE' && /heartbeat/.test(e.message));
  await runtime.writeState({ stopped: true });
  await assert.rejects(request('status', {}, { home: runtime.home, owner: 'tester', reconnectMs: 300 }), (e) => e.code === 'BRIDGE_UNAVAILABLE' && /unloaded/.test(e.message));
  await runtime.stop();
});

test('a heartbeat read that lands inside the runtime replacing state.json is read again, not reported as missing', async () => {
  const runtime = await startFakeRuntime();
  try {
    const file = path.join(runtime.root, 'state.json');
    const body = await fs.readFile(file, 'utf8');
    runtime.freeze();
    await fs.rm(file);
    setTimeout(() => fs.writeFile(file, body), 50);
    const { state } = await readHeartbeat(runtime.home);
    assert.equal(state.sessionId, runtime.state.sessionId);
    await fs.writeFile(file, body.slice(0, 20)); // cut off mid-write
    setTimeout(() => fs.writeFile(file, body), 50);
    assert.equal((await readHeartbeat(runtime.home)).state.sessionId, runtime.state.sessionId);
  } finally {
    await runtime.stop();
  }
});

test('a request made during a Playground reload waits for the new bridge and succeeds', async () => {
  const runtime = await startFakeRuntime();
  try {
    await runtime.writeState({ stopped: true });
    setTimeout(() => runtime.writeState({ stopped: false, sessionId: 'after-reload' }), 800);
    const reply = await request('status', {}, { home: runtime.home, owner: 'tester', timeoutMs: 5000 });
    assert.equal(reply.ok, true);
    assert.equal(runtime.seen[0].sessionId, 'after-reload');
  } finally {
    await runtime.stop();
  }
});

test('a request rejected as STALE_SESSION is sent again once, and other failures are not', async () => {
  let calls = 0;
  const runtime = await startFakeRuntime({
    handler: (op) => {
      calls += 1;
      if (op === 'camera.zoom' && calls === 1) throw new Error('STALE_SESSION: the bridge restarted.');
      if (op === 'asset.patch') throw new Error('PATCH_FAILED: bad value');
      return { calls };
    },
  });
  try {
    const reply = await request('camera.zoom', {}, { home: runtime.home, owner: 'tester', timeoutMs: 3000 });
    assert.deepEqual(reply.data, { calls: 2 });
    const failed = await request('asset.patch', {}, { home: runtime.home, owner: 'tester', timeoutMs: 3000 });
    assert.equal(failed.ok, false);
    assert.equal(runtime.seen.filter((r) => r.operation === 'asset.patch').length, 1);
  } finally {
    await runtime.stop();
  }
});

test('a request withdrawn because the bridge reloaded is resent to the new bridge', async () => {
  const runtime = await startFakeRuntime({ claim: false, handler: () => ({ done: true }) });
  try {
    setTimeout(() => runtime.writeState({ sessionId: 'reloaded' }), 300);
    setTimeout(() => { runtime.control.claim = true; }, 1600); // after the client noticed and withdrew
    const reply = await request('camera.zoom', {}, { home: runtime.home, owner: 'tester', timeoutMs: 10_000 });
    assert.deepEqual(reply.data, { done: true });
    assert.equal(runtime.seen.length, 1, 'the first copy was withdrawn, only the resend reached the bridge');
    assert.equal(runtime.seen[0].sessionId, 'reloaded');
  } finally {
    await runtime.stop();
  }
});

test('a request the new bridge rejects as stale is resent with the new session', async () => {
  const runtime = await startFakeRuntime({ claim: false, handler: () => ({ done: true }) });
  try {
    setTimeout(async () => {
      await runtime.writeState({ sessionId: 'reloaded' });
      runtime.control.claim = true; // claims the old-session request before the client notices
    }, 300);
    const reply = await request('camera.zoom', {}, { home: runtime.home, owner: 'tester', timeoutMs: 10_000 });
    assert.deepEqual(reply.data, { done: true });
    assert.deepEqual(runtime.seen.map((r) => r.sessionId === 'reloaded'), [false, true]);
  } finally {
    await runtime.stop();
  }
});

test('an unclaimed request is withdrawn on timeout and reported NOT_EXECUTED', async () => {
  const runtime = await startFakeRuntime({ claim: false });
  try {
    await assert.rejects(request('camera.zoom', { fieldOfView: 40 }, { home: runtime.home, owner: 'tester', timeoutMs: 1000 }),
      (e) => e instanceof BridgeError && !(e instanceof BridgeUnknownError) && e.code === 'NOT_EXECUTED');
    assert.deepEqual(await listRequests(runtime.root), [], 'the withdrawn request must not stay in the mailbox');
  } finally {
    await runtime.stop();
  }
});

test('a claimed request without an answer is UNKNOWN, never NOT_EXECUTED', async () => {
  const runtime = await startFakeRuntime({ respond: false });
  try {
    await assert.rejects(request('camera.zoom', { fieldOfView: 40 }, { home: runtime.home, owner: 'tester', timeoutMs: 1000 }), (e) => e instanceof BridgeUnknownError);
  } finally {
    await runtime.stop();
  }
});

test('protocol 1 cannot prove non-execution, so a timeout stays UNKNOWN', async () => {
  const runtime = await startFakeRuntime({ protocol: 1, claim: false });
  try {
    await assert.rejects(request('camera.zoom', {}, { home: runtime.home, owner: 'tester', timeoutMs: 1000 }), (e) => e instanceof BridgeUnknownError);
  } finally {
    await runtime.stop();
  }
});

test('a bridge restart while a request waits unclaimed is NOT_EXECUTED without waiting out the timeout', async () => {
  const runtime = await startFakeRuntime({ claim: false });
  try {
    setTimeout(() => runtime.writeState({ sessionId: 'restarted-session' }), 300);
    const started = Date.now();
    await assert.rejects(request('camera.zoom', {}, { home: runtime.home, owner: 'tester', timeoutMs: 30_000, retry: false }), (e) => e.code === 'NOT_EXECUTED');
    assert.ok(Date.now() - started < 5000);
  } finally {
    await runtime.stop();
  }
});

test('a reload that interrupts a running request retries it only when the operation is idempotent', async () => {
  for (const idempotent of [true, false]) {
    const runtime = await startFakeRuntime({ respond: false, handler: () => ({ value: 1 }) });
    try {
      setTimeout(async () => {
        await runtime.writeState({ sessionId: `reloaded-${idempotent}` });
        runtime.control.respond = true; // the new bridge answers; the claimed request died with the old one
      }, 300);
      const attempt = request('camera.get', {}, { home: runtime.home, owner: 'tester', timeoutMs: 10_000, idempotent });
      if (idempotent) {
        assert.deepEqual((await attempt).data, { value: 1 });
        assert.equal(runtime.seen.length, 2);
      } else {
        await assert.rejects(attempt, (e) => e instanceof BridgeUnknownError);
        assert.equal(runtime.seen.length, 1, 'a write with an unknown outcome is never repeated');
      }
    } finally {
      await runtime.stop();
    }
  }
});

test('only PNG files inside the evidence folder can be read back', async () => {
  const runtime = await startFakeRuntime();
  try {
    const inside = path.join(runtime.root, 'evidence', 'shot.png');
    await fs.writeFile(inside, Buffer.from([137, 80, 78, 71]));
    assert.deepEqual([...(await readEvidencePng(runtime.home, inside)).data], [137, 80, 78, 71]);
    await assert.rejects(readEvidencePng(runtime.home, path.join(runtime.home, 'outside.png')), (e) => e.code === 'EVIDENCE_PATH_REJECTED');
    await assert.rejects(readEvidencePng(runtime.home, path.join(runtime.root, 'evidence', '..', 'state.json')), (e) => e.code === 'EVIDENCE_PATH_REJECTED');
  } finally {
    await runtime.stop();
  }
});
