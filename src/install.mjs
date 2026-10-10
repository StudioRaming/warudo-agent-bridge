import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { bridgeHome, appDir, backupsDir, configPath, runtimeDir, timestamp } from './paths.mjs';
import { findWarudo } from './warudo-locate.mjs';
import { CLIENT_IDS, detectClients, registerClient, unregisterClient, registrationStatus, manualSnippets } from './clients.mjs';
import { readHeartbeat } from './transport.mjs';
import { bundleRuntime, RUNTIME_FILE } from './runtime-bundle.mjs';
import { VERSION } from './version.mjs';

export const PACKAGE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const PLAYGROUND_FOLDER = 'WarudoAgentBridge';
export { RUNTIME_FILE };
// 0.1 installed the runtime under this Playground folder.
const LEGACY_PLAYGROUND_FOLDERS = ['StudioRamingAgentBridge'];
const APP_ENTRIES = ['bin', 'src', 'runtime', 'docs', 'package.json', 'LICENSE', 'README.md', 'config.example.json'];
const LEGACY_HOME_ENTRIES = ['node', 'cli', 'bin', 'docs', 'runtime-source', 'install-receipt.json'];

const samePath = (a, b) => path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();

export function mcpEntry(home) {
  return { command: process.execPath, args: [path.join(appDir(home), 'bin', 'warudo-agent-bridge.mjs'), 'mcp'] };
}

// Reads the runtime version from an installed Playground folder (bundle or 0.1 split files).
export function runtimeVersionOf(dir) {
  for (const name of [RUNTIME_FILE, 'AgentBridgePlugin.cs']) {
    try {
      const text = fs.readFileSync(path.join(dir, name), 'utf8');
      const version = (text.match(/const string Version = "([^"]+)"/) || [])[1];
      if (version) return version;
    } catch { /* try the next layout */ }
  }
  return null;
}

function moveToBackup(target, backupRoot, label) {
  if (!fs.existsSync(target)) return null;
  const destination = path.join(backupRoot, label);
  fs.mkdirSync(path.dirname(destination), { recursive: true });
  fs.cpSync(target, destination, { recursive: true });
  fs.rmSync(target, { recursive: true, force: true });
  return destination;
}

// WARUDO watches Playground for *.cs events and recompiles everything once per event.
// A folder rename is not a *.cs event, so folders leave Playground by one rename to a
// staging area on the same volume (outside Playground) and are backed up from there.
function stagingDir(layout) {
  const dir = path.join(path.dirname(layout.streamingAssets), '.warudo-agent-bridge-staging');
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

function moveOutOfPlayground(folder, layout, backupRoot, label) {
  const parked = path.join(stagingDir(layout), `${path.basename(folder)}-${process.pid}-${Date.now()}`);
  try {
    fs.renameSync(folder, parked);
  } catch (error) {
    // A locked file blocks the rename; fall back to per-file removal (costs extra reloads, still correct).
    return { backup: moveToBackup(folder, backupRoot, label), fallback: error.code };
  }
  const backup = moveToBackup(parked, backupRoot, label);
  return { backup };
}

// Installs the runtime as one bundled file. Unchanged: no file event, no reload.
// Changed: old folders leave by rename (no event), the bundle arrives by one rename (one reload).
const countScripts = (dir) => {
  let count = 0;
  const walk = (current) => {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      if (entry.isDirectory()) walk(path.join(current, entry.name));
      else if (entry.name.endsWith('.cs')) count += 1;
    }
  };
  if (fs.existsSync(dir)) walk(dir);
  return count;
};

export function installRuntime(layout, { backupRoot, source = PACKAGE_ROOT, warudoRunning = false, force = false } = {}) {
  const bundle = bundleRuntime(path.join(source, 'runtime'), VERSION);
  const target = path.join(layout.playground, PLAYGROUND_FOLDER);
  const targetFile = path.join(target, RUNTIME_FILE);
  const legacy = LEGACY_PLAYGROUND_FOLDERS.map((name) => path.join(layout.playground, name)).filter((folder) => fs.existsSync(path.join(folder, 'AgentBridgePlugin.cs')));
  const previousVersion = runtimeVersionOf(target) || (legacy.length ? runtimeVersionOf(legacy[0]) : null);
  const current = fs.existsSync(target) ? fs.readdirSync(target) : null;
  const unchanged = legacy.length === 0 && current && current.length === 1 && current[0] === RUNTIME_FILE && fs.readFileSync(targetFile, 'utf8') === bundle;

  // WARUDO's watcher reports every *.cs file that appears or disappears and recompiles all of
  // Playground (several seconds) for each. Removing many old files while WARUDO runs would
  // chain that many recompiles, so that case waits for WARUDO to be closed.
  const removedScripts = unchanged ? 0 : legacy.reduce((sum, folder) => sum + countScripts(folder), 0) +
    (current && !(current.length === 1 && current[0] === RUNTIME_FILE) ? countScripts(target) : 0);
  if (warudoRunning && removedScripts > 1 && !force) {
    return { target: targetFile, changed: false, deferred: true, previousVersion, version: previousVersion, removedScripts, backups: [], strays: [] };
  }

  const moved = [];
  if (!unchanged) {
    for (const folder of legacy) moved.push(moveOutOfPlayground(folder, layout, backupRoot, path.join('playground', path.basename(folder))));
    if (current) moved.push(moveOutOfPlayground(target, layout, backupRoot, path.join('playground', PLAYGROUND_FOLDER)));
    fs.mkdirSync(target, { recursive: true });
    const staged = path.join(stagingDir(layout), `${RUNTIME_FILE}.${process.pid}.tmp`);
    fs.writeFileSync(staged, bundle, 'utf8');
    fs.renameSync(staged, targetFile);
  }
  const strays = findStrayCopies(layout.playground, target);
  return { target: targetFile, changed: !unchanged, previousVersion, version: runtimeVersionOf(target), backups: moved.map((m) => m.backup).filter(Boolean), fallbacks: moved.filter((m) => m.fallback).length, strays };
}

// Uninstall leaves Playground by folder rename, which WARUDO does not notice: the bridge stays
// loaded until WARUDO restarts, instead of forcing a recompile of everyone's Playground scripts.
export function removeRuntime(layout, backupRoot) {
  const target = path.join(layout.playground, PLAYGROUND_FOLDER);
  if (!fs.existsSync(target)) return null;
  return moveOutOfPlayground(target, layout, backupRoot, path.join('playground', PLAYGROUND_FOLDER)).backup;
}

// Another AgentBridgePlugin outside our folder breaks compilation. Report it; never delete user files we do not own.
function findStrayCopies(playground, ours) {
  const strays = [];
  const walk = (dir, depth) => {
    if (depth > 4) return;
    let entries = [];
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (!samePath(full, ours)) walk(full, depth + 1);
      } else if (entry.name.endsWith('.cs')) {
        try {
          if (/partial class AgentBridgePlugin\b/.test(fs.readFileSync(full, 'utf8'))) strays.push(full);
        } catch { /* unreadable */ }
      }
    }
  };
  walk(playground, 0);
  return strays;
}

// True while the WARUDO process that last ran the bridge is alive.
function warudoRunning(home) {
  try {
    const state = JSON.parse(fs.readFileSync(path.join(runtimeDir(home), 'state.json'), 'utf8'));
    process.kill(state.pid, 0);
    return true;
  } catch (error) {
    return error.code === 'EPERM';
  }
}

function installApp(home, backupRoot) {
  const target = appDir(home);
  if (samePath(target, PACKAGE_ROOT)) return { target, copied: false };
  moveToBackup(target, backupRoot, 'app');
  fs.mkdirSync(target, { recursive: true });
  for (const entry of APP_ENTRIES) {
    const from = path.join(PACKAGE_ROOT, entry);
    if (fs.existsSync(from)) fs.cpSync(from, path.join(target, entry), { recursive: true });
  }
  return { target, copied: true };
}

// 0.1 kept the server under <home>/node. Keep that path working as a thin forwarder.
function migrateLegacyHome(home, backupRoot) {
  const legacyServer = path.join(home, 'node', 'mcp-server.mjs');
  if (!fs.existsSync(legacyServer) || fs.readFileSync(legacyServer, 'utf8').includes('warudo-agent-bridge legacy shim')) return null;
  const moved = LEGACY_HOME_ENTRIES.map((entry) => moveToBackup(path.join(home, entry), backupRoot, path.join('legacy-0.1', entry))).filter(Boolean);
  fs.mkdirSync(path.join(home, 'node'), { recursive: true });
  fs.writeFileSync(legacyServer, [
    '#!/usr/bin/env node',
    '// warudo-agent-bridge legacy shim: 0.1 MCP configs pointed here.',
    "process.argv.splice(2, 0, 'mcp');",
    "await import('../app/bin/warudo-agent-bridge.mjs');",
    '',
  ].join('\n'));
  return moved;
}

function writeLaunchers(home) {
  const bin = path.join(home, 'bin');
  fs.mkdirSync(bin, { recursive: true });
  const entry = path.join(appDir(home), 'bin', 'warudo-agent-bridge.mjs');
  const node = process.execPath;
  fs.writeFileSync(path.join(bin, 'warudo-agent.cmd'), `@echo off\r\n"${node}" "${entry}" %*\r\n`);
  fs.writeFileSync(path.join(bin, 'warudo-agent-mcp.cmd'), `@echo off\r\n"${node}" "${entry}" mcp %*\r\n`);
}

function resolveClients(requested, detected) {
  if (!requested || requested === 'auto') return CLIENT_IDS.filter((id) => detected[id]);
  if (requested === 'none') return [];
  const list = requested.split(',').map((value) => value.trim()).filter(Boolean);
  const unknown = list.filter((id) => !CLIENT_IDS.includes(id));
  if (unknown.length) throw new Error(`Unknown client(s): ${unknown.join(', ')}. Use ${CLIENT_IDS.join(', ')}, auto or none.`);
  return list;
}

export function install(options = {}) {
  // Reject invalid client selections before touching installed files or discovering clients.
  resolveClients(options.clients, {});
  const home = bridgeHome(options.home);
  const backupRoot = path.join(backupsDir(home), `${timestamp()}-install`);
  fs.mkdirSync(runtimeDir(home), { recursive: true });
  const report = { version: VERSION, home, backups: backupRoot };

  report.legacyMigrated = migrateLegacyHome(home, backupRoot);
  report.app = installApp(home, backupRoot);
  writeLaunchers(home);
  if (!fs.existsSync(configPath(home))) {
    fs.copyFileSync(path.join(PACKAGE_ROOT, 'config.example.json'), configPath(home));
    report.config = 'created';
  } else {
    report.config = 'kept';
  }

  if (!options.skipRuntime) {
    const layout = findWarudo({ explicit: options.warudo });
    if (!layout) {
      report.runtime = { status: 'warudo-not-found', hint: 'Pass --warudo "<WARUDO install folder>" or set WARUDO_PATH.' };
    } else {
      const result = installRuntime(layout, { backupRoot, warudoRunning: warudoRunning(home), force: options.force === true });
      report.runtime = { status: result.deferred ? 'deferred' : result.changed ? 'installed' : 'unchanged', warudo: layout.root, ...result };
    }
  } else {
    report.runtime = { status: 'skipped' };
  }

  const entry = mcpEntry(home);
  const detected = detectClients();
  report.clients = resolveClients(options.clients, detected).map((client) => registerClient(client, entry, { backupDir: path.join(backupRoot, 'client-configs') }));
  report.manual = manualSnippets(entry);
  fs.writeFileSync(path.join(home, 'install-receipt.json'), `${JSON.stringify({ installedUtc: new Date().toISOString(), ...report }, null, 2)}\n`);
  return report;
}

export function uninstall(options = {}) {
  // Reject invalid client selections before touching installed files or discovering clients.
  resolveClients(options.clients, {});
  const home = bridgeHome(options.home);
  const backupRoot = path.join(backupsDir(home), `${timestamp()}-uninstall`);
  const report = { home, backups: backupRoot };
  const layout = findWarudo({ explicit: options.warudo });
  report.runtime = layout ? { removed: removeRuntime(layout, backupRoot), note: 'Restart WARUDO to unload the bridge.' } : 'warudo-not-found';
  report.clients = resolveClients(options.clients || 'auto', { ...detectClients() }).map((client) => unregisterClient(client, { backupDir: path.join(backupRoot, 'client-configs') }));
  if (options.purge) {
    for (const entry of fs.readdirSync(home)) {
      if (entry !== 'backups') fs.rmSync(path.join(home, entry), { recursive: true, force: true });
    }
    report.purged = true;
  }
  return report;
}

export async function doctor(options = {}) {
  const home = bridgeHome(options.home);
  const checks = [];
  const add = (name, ok, detail) => checks.push({ name, ok, detail });
  const [major] = process.versions.node.split('.').map(Number);
  add('node', major >= 20, `Node ${process.versions.node} (${process.execPath})`);
  add('home', fs.existsSync(home), home);
  const appVersion = (() => {
    try { return JSON.parse(fs.readFileSync(path.join(appDir(home), 'package.json'), 'utf8')).version; } catch { return null; }
  })();
  add('app', appVersion !== null, appVersion ? `${appVersion} at ${appDir(home)}` : 'not installed; run install');
  try {
    const config = JSON.parse(fs.readFileSync(configPath(home), 'utf8'));
    add('config', true, `${configPath(home)} (componentCall.allow: ${(config.componentCall && config.componentCall.allow || []).length} entries)`);
  } catch (error) {
    add('config', false, `${configPath(home)}: ${error.code === 'ENOENT' ? 'missing' : error.message}`);
  }
  const layout = findWarudo({ explicit: options.warudo });
  add('warudo', Boolean(layout), layout ? layout.root : 'not found (use --warudo)');
  if (layout) {
    const runtimeVersion = runtimeVersionOf(path.join(layout.playground, PLAYGROUND_FOLDER));
    const bundled = fs.existsSync(path.join(layout.playground, PLAYGROUND_FOLDER, RUNTIME_FILE));
    add('playground runtime', runtimeVersion === VERSION && bundled, runtimeVersion ? `${runtimeVersion} in ${path.join(layout.playground, PLAYGROUND_FOLDER)}${runtimeVersion === VERSION ? '' : ` (client is ${VERSION}; run install)`}` : 'missing; run install');
    const strays = findStrayCopies(layout.playground, path.join(layout.playground, PLAYGROUND_FOLDER));
    add('no duplicate runtime', strays.length === 0, strays.length ? `remove: ${strays.join(', ')}` : 'ok');
  }
  try {
    const { state, health } = await readHeartbeat(home);
    add('live bridge', state.version === VERSION, `WARUDO pid ${state.pid}, session ${state.sessionId}, scene ${state.scene ?? '-'}, heartbeat ${health.ageMs} ms, runtime ${state.version}${state.version === VERSION ? '' : ` (restart WARUDO or let Playground reload to load ${VERSION})`}`);
  } catch (error) {
    add('live bridge', false, error.message);
  }
  for (const client of CLIENT_IDS) add(`client ${client}`, null, registrationStatus(client));
  return { ok: checks.every((check) => check.ok !== false), checks };
}
