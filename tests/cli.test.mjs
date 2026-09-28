import test from 'node:test';
import assert from 'node:assert/strict';
import { parseArgs } from '../src/cli.mjs';

test('a misspelled option stops the command instead of running the full default', () => {
  // install registers every detected client unless --clients says otherwise, so dropping an
  // unknown flag would change config files the user meant to leave alone.
  assert.throws(() => parseArgs(['install', '--runtime-only']), /Unknown option --runtime-only/);
  assert.throws(() => parseArgs(['install', '--client', 'codex']), /Unknown option --client/);
});

test('options accept both --name value and --name=value', () => {
  assert.deepEqual(parseArgs(['install', '--clients', 'codex', '--skip-runtime']).options, { clients: 'codex', skipRuntime: true });
  assert.deepEqual(parseArgs(['install', '--clients=claude-code,codex']).options, { clients: 'claude-code,codex' });
});

test('-v is a flag that never shifts the operation, and negative numbers stay positional', () => {
  const parsed = parseArgs(['call', '-v', 'character.pose.get']);
  assert.deepEqual(parsed.positional, ['call', 'character.pose.get']);
  assert.equal(parsed.options.version, true);
  assert.deepEqual(parseArgs(['call', 'camera.orbit', '-5']).positional, ['call', 'camera.orbit', '-5']);
});
