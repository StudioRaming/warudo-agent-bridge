import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

// A stand-in for the WARUDO runtime: same mailbox layout and claim protocol.
// handler(operation, params, owner) returns data or throws Error('CODE: message').
export async function startFakeRuntime({ protocol = 2, handler = () => ({}), claim = true, respond = true } = {}) {
  const control = { claim, respond };
  const home = await fs.mkdtemp(path.join(os.tmpdir(), 'wab-test-'));
  const root = path.join(home, 'runtime');
  for (const dir of ['requests', 'responses', 'evidence', 'snapshots']) await fs.mkdir(path.join(root, dir), { recursive: true });
  const state = { protocol, version: 'test', sessionId: crypto.randomUUID().replaceAll('-', ''), pid: process.pid, stopped: false };
  const seen = [];
  let running = true;

  const writeState = async (overrides = {}) => {
    Object.assign(state, overrides);
    await fs.writeFile(path.join(root, 'state.json'), JSON.stringify({ ...state, heartbeatEpoch: Date.now() }));
  };
  await writeState();
  const heartbeat = setInterval(() => { if (running) writeState().catch(() => {}); }, 200);

  const loop = (async () => {
    while (running) {
      let entries = [];
      try { entries = (await fs.readdir(path.join(root, 'requests'))).filter((name) => name.endsWith('.json')); } catch { /* ignore */ }
      for (const name of entries) {
        if (!control.claim) continue;
        const file = path.join(root, 'requests', name);
        const claimed = file.replace(/\.json$/, '.claimed');
        try { await fs.rename(file, protocol >= 2 ? claimed : file); } catch { continue; }
        const body = JSON.parse(await fs.readFile(protocol >= 2 ? claimed : file, 'utf8'));
        seen.push(body);
        if (!control.respond) continue;
        let reply;
        try {
          // Like the real runtime: a request addressed to an older bridge session never runs.
          if (body.sessionId !== state.sessionId) throw new Error('STALE_SESSION: the bridge restarted.');
          reply = { ok: true, data: await handler(body.operation, body.params, body.owner) };
        } catch (error) {
          reply = { ok: false, error: error.message };
        }
        await fs.writeFile(path.join(root, 'responses', name), JSON.stringify({ requestId: body.requestId, sessionId: state.sessionId, operation: body.operation, owner: body.owner, ...reply }));
        await fs.rm(protocol >= 2 ? claimed : file, { force: true });
      }
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  })();

  return {
    home, root, state, seen, writeState, control,
    async stop() {
      running = false;
      clearInterval(heartbeat);
      await loop;
      await fs.rm(home, { recursive: true, force: true });
    },
    freeze() { running = false; clearInterval(heartbeat); },
  };
}

export async function deadPid() {
  const { spawn } = await import('node:child_process');
  const child = spawn(process.execPath, ['-e', '0']);
  const pid = child.pid;
  await new Promise((resolve) => child.on('exit', resolve));
  return pid;
}
