import fs from 'node:fs/promises';
import process from 'node:process';
import { request, responseError, defaultOwner, BridgeUnknownError } from './transport.mjs';
import { TOOLS } from './tools.mjs';
import { runStdioServer } from './mcp-server.mjs';
import { install, uninstall, doctor } from './install.mjs';
import { VERSION } from './version.mjs';

const USAGE = `WARUDO Agent Bridge ${VERSION}

Usage:
  warudo-agent-bridge install [--warudo DIR] [--clients auto|none|claude-code,codex,claude-desktop,cursor] [--skip-runtime] [--force]
  warudo-agent-bridge doctor [--warudo DIR]
  warudo-agent-bridge uninstall [--warudo DIR] [--clients ...] [--purge]
  warudo-agent-bridge mcp [--owner ID] [--timeout-ms MS] [--no-auto-lease]
  warudo-agent-bridge status
  warudo-agent-bridge call OPERATION [JSON] [--params-file PATH | --params-stdin] [--request-id UUID]
  warudo-agent-bridge lease acquire [SECONDS] | lease release
  warudo-agent-bridge tools
  warudo-agent-bridge version

Common options: --home DIR (default %USERPROFILE%\\.warudo-agent-bridge), --owner ID
On Windows prefer --params-file or --params-stdin so shell quoting cannot change the JSON.
`;

const VALUE_OPTIONS = new Set(['--home', '--root', '--owner', '--request-id', '--timeout-ms', '--params-file', '--warudo', '--clients']);
const FLAG_OPTIONS = new Set(['--skip-runtime', '--force', '--purge', '--params-stdin', '--no-auto-lease', '--help']);
const optionKey = (arg) => arg.slice(2).replace(/-([a-z])/g, (_, c) => c.toUpperCase());

// A misspelled option must stop the command: install and uninstall change client configs, so a
// flag that is silently dropped (say --skip-clients) would run the full default instead.
export function parseArgs(argv) {
  const positional = [];
  const options = {};
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    const [name, inline] = arg.startsWith('--') && arg.includes('=') ? [arg.slice(0, arg.indexOf('=')), arg.slice(arg.indexOf('=') + 1)] : [arg, undefined];
    if (VALUE_OPTIONS.has(name)) {
      if (inline === undefined && i + 1 >= argv.length) throw new Error(`${name} needs a value.`);
      options[optionKey(name)] = inline !== undefined ? inline : argv[++i];
    } else if (FLAG_OPTIONS.has(name) && inline === undefined) {
      options[optionKey(name)] = true;
    } else if (arg === '--version' || arg === '-v') {
      options.version = true; // a flag, so it never shifts the positional arguments after it
    } else if (arg === '-h') {
      options.help = true;
    } else if (arg.startsWith('-') && arg !== '-' && !/^-\d/.test(arg)) {
      throw new Error(`Unknown option ${name}.`);
    } else {
      positional.push(arg);
    }
  }
  if (options.root && !options.home) options.home = options.root;
  return { positional, options };
}

const print = (value) => process.stdout.write(`${typeof value === 'string' ? value : JSON.stringify(value, null, 2)}\n`);

async function readStdin() {
  let value = '';
  process.stdin.setEncoding('utf8');
  for await (const chunk of process.stdin) value += chunk;
  return value;
}

async function callOperation(operation, params, options) {
  const owner = options.owner || process.env.WARUDO_AGENT_BRIDGE_OWNER || defaultOwner('cli');
  try {
    const tool = TOOLS.find((entry) => entry.operation === operation && !entry.local);
    const idempotent = operation === 'status' || operation === 'lease.status' || (tool !== undefined && ['read', 'set', 'lease'].includes(tool.kind));
    const response = await request(operation, params, { home: options.home, owner, requestId: options.requestId, timeoutMs: options.timeoutMs && Number(options.timeoutMs), idempotent });
    print(response);
    return responseError(response) ? 1 : 0;
  } catch (error) {
    print({ ok: false, error: { code: error instanceof BridgeUnknownError ? 'UNKNOWN' : (error.code || 'BRIDGE_ERROR'), message: error.message, details: error.details || {} } });
    return error instanceof BridgeUnknownError ? 3 : 2;
  }
}

function printInstallReport(report, options) {
  print(`WARUDO Agent Bridge ${report.version}`);
  print(`  home:     ${report.home}`);
  if (report.legacyMigrated) print(`  migrated 0.1 files to ${report.backups}`);
  const runtime = report.runtime;
  if (runtime.status === 'installed' || runtime.status === 'unchanged') {
    print(`  runtime:  ${runtime.status === 'unchanged' ? 'already up to date' : runtime.target}${runtime.previousVersion && runtime.status === 'installed' ? ` (was ${runtime.previousVersion})` : ''}`);
    for (const stray of runtime.strays) print(`  WARNING: another copy of the bridge is in Playground and will break compilation: ${stray}`);
  } else if (runtime.status === 'deferred') {
    print(`  runtime:  NOT installed yet. WARUDO is running, and replacing ${runtime.removedScripts} old script files would make it`);
    print('            recompile every Playground script once per file. Close WARUDO and run install again (or pass --force).');
  } else {
    print(`  runtime:  ${runtime.status}${runtime.hint ? ` - ${runtime.hint}` : ''}`);
  }
  for (const client of report.clients) {
    print(`  ${client.client.padEnd(15)} ${client.status}${client.detail ? ` (${client.detail})` : ''}${client.note ? ` ${client.note}` : ''}`);
    if (client.status === 'manual' && client.command) print(`      run: ${client.command}`);
  }
  if (!report.clients.length && options.clients !== 'none') print('  no MCP client found. Add this server to your client manually:\n' + report.manual.json);
  if (runtime.status === 'installed') print('\nNext: start WARUDO. A running WARUDO loads the new bridge by itself once Playground recompiles (up to about 30 seconds).');
  if (runtime.status !== 'deferred') print('Open a new agent session and ask it to call warudo_status. Check anything odd with: warudo-agent-bridge doctor');
}

export async function main(argv = process.argv.slice(2)) {
  let parsed;
  try {
    parsed = parseArgs(argv);
  } catch (error) {
    process.stderr.write(`${error.message}\n\n${USAGE}`);
    return 2;
  }
  const { positional, options } = parsed;
  const command = positional[0];
  if (options.help) {
    process.stdout.write(USAGE);
    return 0;
  }
  if (options.version) {
    print(VERSION);
    return 0;
  }

  switch (command) {
    case 'mcp':
      runStdioServer({
        home: options.home,
        owner: options.owner || process.env.WARUDO_AGENT_BRIDGE_OWNER || undefined,
        timeoutMs: options.timeoutMs ? Number(options.timeoutMs) : undefined,
        autoLease: !options.noAutoLease,
      });
      return null; // keep running
    case 'install': {
      try {
        printInstallReport(install(options), options);
        return 0;
      } catch (error) {
        process.stderr.write(`install failed: ${error.message}\n`);
        return 1;
      }
    }
    case 'uninstall':
      print(uninstall(options));
      return 0;
    case 'doctor': {
      const result = await doctor(options);
      for (const check of result.checks) print(`${check.ok === true ? 'OK  ' : check.ok === false ? 'FAIL' : 'INFO'} ${check.name.padEnd(22)} ${check.detail}`);
      return result.ok ? 0 : 1;
    }
    case 'status':
      return callOperation('status', {}, options);
    case 'lease': {
      if (positional[1] === 'acquire') return callOperation('lease.acquire', positional[2] !== undefined ? { seconds: Number(positional[2]) } : {}, options);
      if (positional[1] === 'release') return callOperation('lease.release', {}, options);
      break;
    }
    case 'call': {
      const operation = positional[1];
      if (!operation) break;
      let params = {};
      try {
        if (options.paramsFile && options.paramsStdin) throw new Error('Choose only one of --params-file or --params-stdin.');
        const raw = options.paramsFile ? await fs.readFile(options.paramsFile, 'utf8') : options.paramsStdin ? await readStdin() : positional[2];
        if (raw !== undefined) params = JSON.parse(raw.replace(/^﻿/, ''));
      } catch (error) {
        print({ ok: false, error: { code: 'INVALID_JSON', message: error.message } });
        return 2;
      }
      return callOperation(operation, params, options);
    }
    case 'tools':
      for (const tool of TOOLS) print(`${tool.name.padEnd(32)} ${tool.operation.padEnd(22)} ${tool.kind}`);
      return 0;
    case 'version':
      print(VERSION);
      return 0;
    default:
      break;
  }
  process.stderr.write(USAGE);
  return command ? 2 : 0;
}
