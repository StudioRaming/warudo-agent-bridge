import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { parseLibraryFolders, findWarudo, warudoLayout } from '../src/warudo-locate.mjs';
import { upsertCodexServer, removeCodexServer, upsertJsonServer, removeJsonServer } from '../src/clients.mjs';
import { installRuntime, PLAYGROUND_FOLDER, RUNTIME_FILE, runtimeVersionOf } from '../src/install.mjs';
import { VERSION } from '../src/version.mjs';

const temp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'wab-install-'));

function fakeWarudo(base) {
  const root = path.join(base, 'steamapps', 'common', 'Warudo');
  fs.mkdirSync(path.join(root, 'Warudo_Data', 'StreamingAssets', 'Playground'), { recursive: true });
  fs.mkdirSync(path.join(root, 'Warudo_Data', 'Managed'), { recursive: true });
  return root;
}

test('Steam library folders are parsed, including escaped Windows paths', () => {
  const vdf = '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"C:\\\\Program Files (x86)\\\\Steam"\n\t}\n\t"1"\n\t{\n\t\t"path"\t\t"D:\\\\SteamLibrary"\n\t\t"apps" { "2079120" "1" }\n\t}\n}';
  assert.deepEqual(parseLibraryFolders(vdf), ['C:\\Program Files (x86)\\Steam', 'D:\\SteamLibrary']);
});

test('WARUDO is found in a secondary Steam library and from any of its folders', () => {
  const base = temp();
  const steam = path.join(base, 'Steam');
  const library = path.join(base, 'Library');
  fs.mkdirSync(path.join(steam, 'steamapps'), { recursive: true });
  fs.writeFileSync(path.join(steam, 'steamapps', 'libraryfolders.vdf'), `"libraryfolders" { "1" { "path" "${library.replace(/\\/g, '\\\\')}" } }`);
  const root = fakeWarudo(library);
  assert.equal(findWarudo({ steamRoots: [steam] }).root, root);
  const playground = path.join(root, 'Warudo_Data', 'StreamingAssets', 'Playground');
  assert.equal(warudoLayout(playground).root, root);
  assert.equal(warudoLayout(path.dirname(playground)).root, root);
  assert.equal(warudoLayout(path.join(base, 'nothing')), null);
  fs.rmSync(base, { recursive: true, force: true });
});

test('Codex registration replaces both 0.1 spellings and leaves other settings byte-identical', () => {
  const before = [
    'model = "gpt-5"',
    '',
    '[mcp_servers.warudo_agent_bridge]',
    "command = 'node'",
    "args = ['old.mjs']",
    '',
    '[mcp_servers.other]',
    'command = "x"',
    '',
    '[mcp_servers.warudo-agent-bridge]',
    "command = 'node'",
    '',
    '[mcp_servers.warudo-agent-bridge.env]',
    'A = "1"',
    '',
    '[features]',
    'x = true',
    '',
  ].join('\r\n');
  const entry = { command: 'C:\\Program Files\\nodejs\\node.exe', args: ['C:\\Users\\me\\.warudo-agent-bridge\\app\\bin\\warudo-agent-bridge.mjs', 'mcp'] };
  const { text, replaced } = upsertCodexServer(before, entry);
  assert.equal(replaced, true);
  assert.equal((text.match(/\[mcp_servers\.warudo[-_]agent[-_]bridge/g) || []).length, 1);
  assert.ok(text.includes("[mcp_servers.other]\r\ncommand = \"x\""));
  assert.ok(text.includes('[features]\r\nx = true'));
  assert.ok(text.startsWith('model = "gpt-5"\r\n'));
  assert.ok(text.includes("command = 'C:\\Program Files\\nodejs\\node.exe'"), 'Windows paths use TOML literal strings');
  assert.equal(upsertCodexServer(text, entry).text, text, 'registration is idempotent');
  assert.equal(removeCodexServer(text).removed, 1);
});

test('re-registering in Codex keeps the comment that introduces the next server', () => {
  const before = [
    '[mcp_servers.warudo-agent-bridge]',
    '# a note inside the bridge table',
    "command = 'node'",
    '',
    '',
    '# Blender MCP: added by hand',
    '[mcp_servers.blender]',
    'command = "python"',
    '',
  ].join('\n');
  const { text } = removeCodexServer(before);
  assert.equal(text, ['', '', '# Blender MCP: added by hand', '[mcp_servers.blender]', 'command = "python"', ''].join('\n'));
  assert.ok(upsertCodexServer(before, { command: 'node', args: ['a.mjs', 'mcp'] }).text.includes('# Blender MCP: added by hand\n[mcp_servers.blender]'));
});

test('a bridge table header quoted inside another server\'s multi-line string is left alone', () => {
  const other = [
    '[mcp_servers.notes]',
    'command = "notes"',
    'prompt = """',
    '[mcp_servers.warudo-agent-bridge]',
    'command = "not a real table"',
    '"""',
    "extra = '''",
    '[mcp_servers.warudo-agent-bridge]',
    "'''",
  ];
  const before = [...other, '', '[mcp_servers.warudo-agent-bridge]', "command = 'node'", ''].join('\n');
  const { text, removed } = removeCodexServer(before);
  assert.equal(removed, 1);
  assert.equal(text, [...other, '', ''].join('\n'));
});

test('JSON client configs keep other servers', () => {
  const before = JSON.stringify({ globalShortcut: 'x', mcpServers: { other: { command: 'y' } } });
  const { text } = upsertJsonServer(before, { command: 'node', args: ['a'] });
  const parsed = JSON.parse(text);
  assert.deepEqual(parsed.mcpServers.other, { command: 'y' });
  assert.deepEqual(parsed.mcpServers['warudo-agent-bridge'], { command: 'node', args: ['a'] });
  assert.equal(parsed.globalShortcut, 'x');
  assert.equal(JSON.parse(removeJsonServer(text).text).mcpServers['warudo-agent-bridge'], undefined);
  assert.throws(() => upsertJsonServer('[1]', { command: 'n', args: [] }));
});

test('runtime install moves a 0.1 copy to backups and reports foreign copies without deleting them', () => {
  const base = temp();
  const layout = warudoLayout(fakeWarudo(base));
  const legacy = path.join(layout.playground, 'StudioRamingAgentBridge');
  fs.mkdirSync(legacy);
  fs.writeFileSync(path.join(legacy, 'AgentBridgePlugin.cs'), 'const string Version = "0.1.0"; public sealed partial class AgentBridgePlugin {}');
  const foreign = path.join(layout.playground, 'MyStuff');
  fs.mkdirSync(foreign);
  fs.writeFileSync(path.join(foreign, 'Copy.cs'), 'public sealed partial class AgentBridgePlugin {}');
  fs.writeFileSync(path.join(layout.playground, 'Unrelated.cs'), 'class Unrelated {}');
  const backupRoot = path.join(base, 'backups');

  const result = installRuntime(layout, { backupRoot });
  assert.equal(result.changed, true);
  assert.equal(result.previousVersion, '0.1.0');
  assert.equal(result.version, VERSION);
  assert.equal(fs.existsSync(legacy), false);
  assert.ok(fs.existsSync(path.join(backupRoot, 'playground', 'StudioRamingAgentBridge', 'AgentBridgePlugin.cs')));
  assert.deepEqual(result.strays, [path.join(foreign, 'Copy.cs')]);
  assert.ok(fs.existsSync(path.join(layout.playground, 'Unrelated.cs')));
  // One bundled file: WARUDO recompiles once per *.cs event, so one file means one reload.
  assert.deepEqual(fs.readdirSync(path.join(layout.playground, PLAYGROUND_FOLDER)), [RUNTIME_FILE]);
  assert.equal(runtimeVersionOf(path.join(layout.playground, PLAYGROUND_FOLDER)), VERSION);
  // Nothing is parked inside Playground; staging lives beside StreamingAssets.
  assert.deepEqual(fs.readdirSync(layout.playground).sort(), ['MyStuff', PLAYGROUND_FOLDER, 'Unrelated.cs'].sort());
  fs.rmSync(base, { recursive: true, force: true });
});

test('reinstalling an unchanged runtime touches no file in Playground', () => {
  const base = temp();
  const layout = warudoLayout(fakeWarudo(base));
  installRuntime(layout, { backupRoot: path.join(base, 'b1') });
  const file = path.join(layout.playground, PLAYGROUND_FOLDER, RUNTIME_FILE);
  const before = fs.statSync(file).mtimeMs;
  const again = installRuntime(layout, { backupRoot: path.join(base, 'b2') });
  assert.equal(again.changed, false);
  assert.equal(fs.statSync(file).mtimeMs, before);
  assert.equal(fs.existsSync(path.join(base, 'b2')), false, 'no backup when nothing changed');
  fs.rmSync(base, { recursive: true, force: true });
});
