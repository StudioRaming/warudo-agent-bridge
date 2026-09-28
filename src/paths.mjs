import os from 'node:os';
import path from 'node:path';

// Every file the bridge owns lives under one home directory. The WARUDO runtime
// reads the same WARUDO_AGENT_BRIDGE_HOME variable, so both halves agree.
export function bridgeHome(explicitHome) {
  if (explicitHome) return path.resolve(explicitHome);
  const configured = process.env.WARUDO_AGENT_BRIDGE_HOME;
  return path.resolve(configured || path.join(os.homedir(), '.warudo-agent-bridge'));
}

export const runtimeDir = (home) => path.join(bridgeHome(home), 'runtime');
export const appDir = (home) => path.join(bridgeHome(home), 'app');
export const backupsDir = (home) => path.join(bridgeHome(home), 'backups');
export const configPath = (home) => path.join(bridgeHome(home), 'config.json');

export function timestamp(date = new Date()) {
  const pad = (value) => String(value).padStart(2, '0');
  return `${date.getFullYear()}${pad(date.getMonth() + 1)}${pad(date.getDate())}-${pad(date.getHours())}${pad(date.getMinutes())}${pad(date.getSeconds())}`;
}
