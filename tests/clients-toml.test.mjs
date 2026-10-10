import assert from 'node:assert/strict';
import test from 'node:test';
import { removeCodexServer, upsertCodexServer } from '../src/clients.mjs';

for (const line of ['# document """ syntax', "# document ''' syntax", 'model = "x" # """', 'note = \'"""\'', "note = \"'''\""]) {
  test('quote markers outside multiline strings do not hide tables: ' + line, () => {
    const prefix = line + '\n';
    const before = prefix + '[mcp_servers.warudo-agent-bridge]\ncommand = "old"\n[features]\nx = true\n';
    const result = removeCodexServer(before);
    assert.equal(result.removed, 1);
    assert.equal(result.text, prefix + '[features]\nx = true\n');
    const entry = { command: 'node', args: ['bridge.mjs'] };
    const updated = upsertCodexServer(before, entry).text;
    assert.equal(upsertCodexServer(updated, entry).text, updated);
  });
}

test('real multiline strings keep embedded table headers', () => {
  const prefix = 'notes = """\n[mcp_servers.warudo-agent-bridge]\n# this is string content\n"""\n';
  const result = removeCodexServer(prefix + '[mcp_servers.warudo-agent-bridge]\ncommand = "old"\n');
  assert.equal(result.removed, 1);
  assert.equal(result.text, prefix);
});

test('escaped quotes do not close a multiline basic string', () => {
  const prefix = ['notes = """', String.raw`escaped \""" marker`, '[mcp_servers.warudo-agent-bridge]', '"""', ''].join('\n');
  const result = removeCodexServer(prefix + '[mcp_servers.warudo-agent-bridge]\ncommand = "old"\n');
  assert.equal(result.removed, 1);
  assert.equal(result.text, prefix);
});

for (const quote of ['"', "'"]) {
  test('four and five quote endings resume table parsing: ' + quote, () => {
    for (const count of [4, 5]) {
      const prefix = 'notes = ' + quote.repeat(3) + '\ntext' + quote.repeat(count) + '\n';
      const result = removeCodexServer(prefix + '[mcp_servers.warudo-agent-bridge]\ncommand = "old"\n');
      assert.equal(result.removed, 1);
      assert.equal(result.text, prefix);
    }
  });
}

test('comment quote markers inside a removed table do not consume the next table', () => {
  const before = '[mcp_servers.warudo-agent-bridge]\ncommand = "old" # """\n[features]\nx = true\n';
  assert.equal(removeCodexServer(before).text, '[features]\nx = true\n');
});
