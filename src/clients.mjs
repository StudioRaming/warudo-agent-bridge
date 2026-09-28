import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export const SERVER_NAME = 'warudo-agent-bridge';
export const CLIENT_IDS = ['claude-code', 'codex', 'claude-desktop', 'cursor'];

const codexHome = () => process.env.CODEX_HOME || path.join(os.homedir(), '.codex');
const claudeDesktopConfig = () => path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'Claude', 'claude_desktop_config.json');
const cursorConfig = () => path.join(os.homedir(), '.cursor', 'mcp.json');

function backupFile(file, backupDir) {
  if (!fs.existsSync(file)) return null;
  fs.mkdirSync(backupDir, { recursive: true });
  const target = path.join(backupDir, `${path.basename(path.dirname(file))}-${path.basename(file)}`);
  fs.copyFileSync(file, target);
  return target;
}

function writeAtomic(file, text) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(temp, text, 'utf8');
  fs.renameSync(temp, file);
}

// ---- Codex (config.toml) ----------------------------------------------------

const tomlString = (value) => (/['\r\n]/.test(value) ? JSON.stringify(value) : `'${value}'`);

function codexSectionOwner(line) {
  const match = line.match(/^\s*\[\s*mcp_servers\s*\.\s*(?:"([^"]+)"|'([^']+)'|([A-Za-z0-9_-]+))/);
  return match ? (match[1] ?? match[2] ?? match[3]) : null;
}

// Tracks TOML multi-line strings ("""...""" and '''...''') across lines: a line inside one is
// string content, never a table header, even when it starts with "[".
function multiLineState(line, open) {
  let state = open;
  for (let i = 0; i + 3 <= line.length; i += 1) {
    const three = line.slice(i, i + 3);
    if (three !== '"""' && three !== "'''") continue;
    if (state === null) state = three;
    else if (state === three) state = null;
    else continue;
    i += 2;
  }
  return state;
}

// Removes every [mcp_servers.<name>] table (and its sub-tables) for the bridge,
// including the 0.1 underscore spelling. Other bytes are left untouched: comments and
// blank lines after the bridge's last key belong to whatever follows, so they stay.
export function removeCodexServer(text) {
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  const lines = text.split(/\r?\n/);
  const names = new Set([SERVER_NAME, SERVER_NAME.replaceAll('-', '_')]);
  const kept = [];
  let skipping = false;
  let pending = [];
  let removed = 0;
  let multi = null;
  for (const line of lines) {
    const inString = multi !== null;
    multi = multiLineState(line, multi);
    if (!inString && /^\s*\[/.test(line)) {
      const owner = codexSectionOwner(line);
      const ours = owner !== null && names.has(owner);
      if (skipping && !ours) kept.push(...pending);
      pending = [];
      skipping = ours;
      if (ours) removed += 1;
    } else if (skipping) {
      if (!inString && /^\s*(#.*)?$/.test(line)) pending.push(line);
      else pending = []; // a key: the comments above it were inside the bridge's table
      continue;
    }
    if (!skipping) kept.push(line);
  }
  if (skipping) kept.push(...pending);
  return { text: kept.join(eol), removed };
}

export function upsertCodexServer(text, { command, args, cwd }) {
  const { text: base, removed } = removeCodexServer(text);
  const eol = base.includes('\r\n') ? '\r\n' : '\n';
  const block = [
    `[mcp_servers.${SERVER_NAME}]`,
    `command = ${tomlString(command)}`,
    `args = [${args.map(tomlString).join(', ')}]`,
    ...(cwd ? [`cwd = ${tomlString(cwd)}`] : []),
    'startup_timeout_sec = 20',
    // Codex cuts tool calls at 60 s by default; opening a scene or waiting for a load can take longer.
    'tool_timeout_sec = 180',
    'enabled = true',
  ].join(eol);
  const trimmed = base.replace(/(\r?\n)+$/, '');
  return { text: `${trimmed}${trimmed ? eol + eol : ''}${block}${eol}`, replaced: removed > 0 };
}

// ---- JSON configs (Claude Desktop, Cursor) --------------------------------

export function upsertJsonServer(text, entry) {
  const config = text && text.trim() ? JSON.parse(text) : {};
  if (typeof config !== 'object' || Array.isArray(config)) throw new Error('config root is not an object');
  const servers = config.mcpServers && typeof config.mcpServers === 'object' ? config.mcpServers : {};
  const replaced = Object.prototype.hasOwnProperty.call(servers, SERVER_NAME);
  config.mcpServers = { ...servers, [SERVER_NAME]: entry };
  return { text: `${JSON.stringify(config, null, 2)}\n`, replaced };
}

export function removeJsonServer(text) {
  if (!text || !text.trim()) return { text, removed: false };
  const config = JSON.parse(text);
  if (!config.mcpServers || !Object.prototype.hasOwnProperty.call(config.mcpServers, SERVER_NAME)) return { text, removed: false };
  delete config.mcpServers[SERVER_NAME];
  return { text: `${JSON.stringify(config, null, 2)}\n`, removed: true };
}

// ---- Claude Code (CLI) -------------------------------------------------------

function runClaude(args) {
  let result = spawnSync('claude', args, { encoding: 'utf8', windowsHide: true, timeout: 60_000 });
  if (result.error && result.error.code === 'ENOENT' && process.platform === 'win32') {
    // npm installs claude as a .cmd shim, which needs a shell.
    const quoted = ['claude', ...args].map((part) => (/[\s"&|<>^]/.test(part) ? `"${part.replace(/"/g, '""')}"` : part)).join(' ');
    result = spawnSync(quoted, { encoding: 'utf8', windowsHide: true, timeout: 60_000, shell: true });
  }
  return result;
}

const claudeAvailable = () => {
  const result = runClaude(['--version']);
  return !result.error && result.status === 0;
};

// ---- Public API ------------------------------------------------------------

export function detectClients() {
  return {
    'claude-code': claudeAvailable(),
    codex: fs.existsSync(codexHome()),
    'claude-desktop': fs.existsSync(path.dirname(claudeDesktopConfig())),
    cursor: fs.existsSync(path.dirname(cursorConfig())),
  };
}

export function manualSnippets(entry) {
  const quote = (value) => (/\s/.test(value) ? `"${value}"` : value);
  return {
    'claude-code': `claude mcp add -s user ${SERVER_NAME} -- ${[entry.command, ...entry.args].map(quote).join(' ')}`,
    codex: upsertCodexServer('', entry).text.trim(),
    json: JSON.stringify({ mcpServers: { [SERVER_NAME]: { command: entry.command, args: entry.args } } }, null, 2),
  };
}

export function registerClient(client, entry, { backupDir }) {
  try {
    if (client === 'claude-code') {
      if (!claudeAvailable()) return { client, status: 'manual', detail: 'claude CLI not found on PATH', command: manualSnippets(entry)['claude-code'] };
      const existed = runClaude(['mcp', 'get', SERVER_NAME]).status === 0;
      if (existed) runClaude(['mcp', 'remove', SERVER_NAME, '-s', 'user']);
      const added = runClaude(['mcp', 'add', '-s', 'user', SERVER_NAME, '--', entry.command, ...entry.args]);
      if (added.status !== 0) return { client, status: 'failed', detail: (added.stderr || added.stdout || String(added.error || '')).trim() };
      return { client, status: existed ? 'updated' : 'registered', detail: 'user scope' };
    }
    if (client === 'codex') {
      const file = path.join(codexHome(), 'config.toml');
      const before = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
      const { text, replaced } = upsertCodexServer(before, entry);
      if (text === before) return { client, status: 'unchanged', file };
      const backup = backupFile(file, backupDir);
      writeAtomic(file, text);
      return { client, status: replaced ? 'updated' : 'registered', file, backup };
    }
    const file = client === 'claude-desktop' ? claudeDesktopConfig() : client === 'cursor' ? cursorConfig() : null;
    if (!file) return { client, status: 'failed', detail: `unknown client ${client}` };
    const before = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
    const { text, replaced } = upsertJsonServer(before, { command: entry.command, args: entry.args });
    if (text === before) return { client, status: 'unchanged', file };
    const backup = backupFile(file, backupDir);
    writeAtomic(file, text);
    return { client, status: replaced ? 'updated' : 'registered', file, backup, ...(client === 'claude-desktop' ? { note: 'Restart Claude Desktop to load it.' } : {}) };
  } catch (error) {
    return { client, status: 'failed', detail: error.message };
  }
}

export function unregisterClient(client, { backupDir }) {
  try {
    if (client === 'claude-code') {
      if (!claudeAvailable()) return { client, status: 'manual', command: `claude mcp remove ${SERVER_NAME} -s user` };
      const removed = runClaude(['mcp', 'remove', SERVER_NAME, '-s', 'user']);
      return { client, status: removed.status === 0 ? 'removed' : 'absent' };
    }
    const file = client === 'codex' ? path.join(codexHome(), 'config.toml') : client === 'claude-desktop' ? claudeDesktopConfig() : cursorConfig();
    if (!fs.existsSync(file)) return { client, status: 'absent' };
    const before = fs.readFileSync(file, 'utf8');
    const result = client === 'codex' ? removeCodexServer(before) : removeJsonServer(before);
    const removed = client === 'codex' ? result.removed > 0 : result.removed;
    if (!removed) return { client, status: 'absent' };
    const backup = backupFile(file, backupDir);
    writeAtomic(file, result.text);
    return { client, status: 'removed', file, backup };
  } catch (error) {
    return { client, status: 'failed', detail: error.message };
  }
}

export function registrationStatus(client) {
  try {
    if (client === 'claude-code') {
      if (!claudeAvailable()) return 'cli-missing';
      const result = runClaude(['mcp', 'get', SERVER_NAME]);
      return result.status === 0 ? `registered: ${(result.stdout.match(/Args: (.*)/) || [])[1] || ''}`.trim() : 'not registered';
    }
    const file = client === 'codex' ? path.join(codexHome(), 'config.toml') : client === 'claude-desktop' ? claudeDesktopConfig() : cursorConfig();
    if (!fs.existsSync(file)) return 'no config';
    const text = fs.readFileSync(file, 'utf8');
    if (client === 'codex') return removeCodexServer(text).removed > 0 ? 'registered' : 'not registered';
    const config = JSON.parse(text);
    return config.mcpServers && config.mcpServers[SERVER_NAME] ? 'registered' : 'not registered';
  } catch (error) {
    return `error: ${error.message}`;
  }
}
