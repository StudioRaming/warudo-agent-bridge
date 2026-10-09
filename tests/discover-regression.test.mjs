import assert from 'node:assert/strict';
import test from 'node:test';
import { createServer, MODERN_VERSIONS, LEGACY_VERSIONS } from '../src/mcp-server.mjs';

// Exercises the real JSON-RPC handler; no runtime mailbox/tool calls.
for (const version of MODERN_VERSIONS) {
  test(`discover has required cache metadata (${version})`, async () => {
    const server = createServer({ home: '/tmp/unused-discovery-regression' });
    const reply = await server.handle({ jsonrpc: '2.0', id: 7,
      method: 'server/discover', params: { _meta: {
        'io.modelcontextprotocol/protocolVersion': version,
      } } });
    assert.equal(reply.id, 7);
    assert.equal(reply.error, undefined);
    const result = reply.result;
    assert.equal(result.resultType, 'complete');
    assert.equal(result.ttlMs, 0);
    assert.ok(Number.isInteger(result.ttlMs));
    assert.equal(result.cacheScope, 'private');
    assert.deepEqual(result.supportedVersions, [...MODERN_VERSIONS, ...LEGACY_VERSIONS]);
    assert.equal(result.capabilities.tools.listChanged, false);
    assert.equal(result._meta['io.modelcontextprotocol/serverInfo'].name, 'warudo-agent-bridge');
    assert.ok(result.instructions.length > 0);
  });
}

test('legacy discovery keeps its existing shape', async () => {
  const server = createServer({ home: '/tmp/unused-discovery-regression' });
  const { result } = await server.handle({ jsonrpc: '2.0', id: 8,
    method: 'server/discover', params: {} });
  for (const key of ['ttlMs', 'cacheScope', 'resultType'])
    assert.equal(key in result, false, key);
  assert.ok(result.supportedVersions.length > 0);
});

test('all modern lists preserve existing cache metadata', async () => {
  const server = createServer({ home: '/tmp/unused-discovery-regression' });
  for (const method of ['tools/list', 'prompts/list', 'resources/list', 'resources/templates/list']) {
    const { result } = await server.handle({ jsonrpc: '2.0', id: 9, method,
      params: { _meta: { 'io.modelcontextprotocol/protocolVersion': MODERN_VERSIONS[0] } } });
    assert.equal(result.ttlMs, 0, method);
    assert.equal(result.cacheScope, 'private', method);
  }
});
