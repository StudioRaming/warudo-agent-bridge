import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { readEvidencePng } from '../src/transport.mjs';

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'wab-evidence-boundary-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const home = path.join(root, 'home');
  const evidence = path.join(home, 'runtime', 'evidence');
  await fs.mkdir(evidence, { recursive: true });
  const inside = path.join(evidence, 'shot.png');
  const outside = path.join(root, 'outside.png');
  await fs.writeFile(inside, 'inside fixture');
  await fs.writeFile(outside, 'outside fixture');
  return { root, home, evidence, inside, outside };
}

async function linkOrSkip(t, target, link, type) {
  try { await fs.symlink(target, link, type); return true; }
  catch (error) {
    if (['EPERM', 'EACCES', 'ENOTSUP'].includes(error.code)) {
      t.skip('This environment cannot create the fixture symlink.');
      return false;
    }
    throw error;
  }
}

test('ordinary evidence PNG remains readable', async (t) => {
  const f = await fixture(t);
  assert.equal((await readEvidencePng(f.home, f.inside)).data.toString(), 'inside fixture');
});

test('nested evidence PNG remains readable', async (t) => {
  const f = await fixture(t);
  const nested = path.join(f.evidence, 'nested');
  await fs.mkdir(nested);
  const file = path.join(nested, 'shot.png');
  await fs.writeFile(file, 'nested fixture');
  assert.equal((await readEvidencePng(f.home, file)).data.toString(), 'nested fixture');
});

test('lexically outside evidence remains rejected', async (t) => {
  const f = await fixture(t);
  await assert.rejects(readEvidencePng(f.home, f.outside), { code: 'EVIDENCE_PATH_REJECTED' });
});

test('missing evidence retains the read-failure classification', async (t) => {
  const f = await fixture(t);
  await assert.rejects(readEvidencePng(f.home, path.join(f.evidence, 'missing.png')),
    { code: 'EVIDENCE_READ_FAILED' });
});

test('file symlink cannot expose a PNG outside evidence', async (t) => {
  const f = await fixture(t);
  const link = path.join(f.evidence, 'linked.png');
  if (!await linkOrSkip(t, f.outside, link, 'file')) return;
  await assert.rejects(readEvidencePng(f.home, link), { code: 'EVIDENCE_PATH_REJECTED' });
});

test('directory junction or symlink cannot escape evidence', async (t) => {
  const f = await fixture(t);
  const link = path.join(f.evidence, 'linked-directory');
  if (!await linkOrSkip(t, f.root, link, process.platform === 'win32' ? 'junction' : 'dir')) return;
  await assert.rejects(readEvidencePng(f.home, path.join(link, 'outside.png')),
    { code: 'EVIDENCE_PATH_REJECTED' });
});

test('symlink to another PNG inside evidence remains readable', async (t) => {
  const f = await fixture(t);
  const link = path.join(f.evidence, 'alias.png');
  if (!await linkOrSkip(t, f.inside, link, 'file')) return;
  assert.equal((await readEvidencePng(f.home, link)).data.toString(), 'inside fixture');
});
