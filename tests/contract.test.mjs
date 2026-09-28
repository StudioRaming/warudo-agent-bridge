// The MCP server and the WARUDO runtime ship separately. These checks catch the two halves drifting apart.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import { TOOLS } from '../src/tools.mjs';
import { VERSION } from '../src/version.mjs';

const runtimeDir = new URL('../runtime/', import.meta.url);
const core = fs.readFileSync(new URL('AgentBridgePlugin.cs', runtimeDir), 'utf8');
const leaseFree = new Set([...core.match(/LeaseFreeOperations = new HashSet<string>\s*\{([\s\S]*?)\};/)[1].matchAll(/"([^"]+)"/g)].map((m) => m[1]));

test('every tool operation is dispatched by the runtime', () => {
  const cases = new Set([...core.matchAll(/case "([a-z._]+)":/g)].map((m) => m[1]));
  const missing = TOOLS.filter((tool) => !cases.has(tool.operation)).map((tool) => `${tool.name} -> ${tool.operation}`);
  assert.deepEqual(missing, []);
});

test('tools marked as lease-free are exactly the operations the runtime runs without a lease', () => {
  for (const tool of TOOLS) {
    // The runtime frees these only for some arguments (a pick that does not select, a save that
    // does not overwrite); the MCP server takes the lease when the runtime asks for it.
    if (tool.kind === 'lease' || tool.operation === 'viewport.pick' || tool.operation === 'character.pose.save') continue;
    assert.equal(!tool.needsLease, leaseFree.has(tool.operation), `${tool.name} (${tool.kind}) disagrees with LeaseFreeOperations`);
  }
});

test('package, runtime constant and plugin attribute carry the same version', () => {
  assert.equal(core.match(/const string Version = "([^"]+)"/)[1], VERSION);
  assert.equal(core.match(/\[PluginType\([^\]]*Version = "([^"]+)"/)[1], VERSION);
});
