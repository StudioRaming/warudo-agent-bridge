import test from 'node:test';
import assert from 'node:assert/strict';
import { parseArgs } from '../src/cli.mjs';

test('missing values cannot consume the next option', () => {
  for (const option of ['--home', '--root', '--owner', '--request-id', '--timeout-ms', '--params-file', '--warudo', '--clients']) {
    for (const next of ['--skip-runtime', '--clients=none', '--purge', '--help', '--version', '-h', '-v', '--typo']) {
      assert.throws(() => parseArgs(['install', option, next]), /needs a value/, option + ' ' + next);
    }
  }
});

test('explicit inline option-like values and negative numbers stay supported', () => {
  assert.equal(parseArgs(['call', '--owner=--literal']).options.owner, '--literal');
  assert.equal(parseArgs(['mcp', '--timeout-ms', '-5']).options.timeoutMs, '-5');
  assert.equal(parseArgs(['call', '--params-file', '-']).options.paramsFile, '-');
});
