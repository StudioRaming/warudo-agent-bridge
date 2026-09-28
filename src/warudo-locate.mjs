import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const WARUDO_DIR = path.join('steamapps', 'common', 'Warudo');

// libraryfolders.vdf lists every Steam library as "path"  "D:\\SteamLibrary".
export function parseLibraryFolders(text) {
  const paths = [];
  for (const match of String(text).matchAll(/"path"\s+"((?:[^"\\]|\\.)*)"/g)) {
    paths.push(match[1].replace(/\\\\/g, '\\').replace(/\\"/g, '"'));
  }
  return paths;
}

function registrySteamPaths() {
  if (process.platform !== 'win32') return [];
  const found = [];
  for (const [key, value] of [['HKCU\\Software\\Valve\\Steam', 'SteamPath'], ['HKLM\\SOFTWARE\\WOW6432Node\\Valve\\Steam', 'InstallPath'], ['HKLM\\SOFTWARE\\Valve\\Steam', 'InstallPath']]) {
    try {
      const output = execFileSync('reg', ['query', key, '/v', value], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
      const match = output.match(/REG_SZ\s+(.+)\s*$/m);
      if (match) found.push(path.normalize(match[1].trim()));
    } catch { /* key absent */ }
  }
  return found;
}

export function steamLibraries({ steamRoots } = {}) {
  const roots = steamRoots || [...registrySteamPaths(), 'C:\\Program Files (x86)\\Steam', 'C:\\Program Files\\Steam'];
  const libraries = new Set();
  for (const root of roots) {
    if (!fs.existsSync(root)) continue;
    libraries.add(path.normalize(root));
    try {
      const vdf = fs.readFileSync(path.join(root, 'steamapps', 'libraryfolders.vdf'), 'utf8');
      for (const library of parseLibraryFolders(vdf)) libraries.add(path.normalize(library));
    } catch { /* no library file */ }
  }
  return [...libraries];
}

// Accepts the WARUDO install folder, Warudo_Data, StreamingAssets or Playground.
export function warudoLayout(candidate) {
  if (!candidate) return null;
  let dir = path.resolve(candidate);
  const base = path.basename(dir).toLowerCase();
  if (base === 'playground') dir = path.dirname(dir);
  if (path.basename(dir).toLowerCase() === 'streamingassets') dir = path.dirname(dir);
  if (path.basename(dir).toLowerCase() === 'warudo_data') dir = path.dirname(dir);
  const data = path.join(dir, 'Warudo_Data');
  const streamingAssets = path.join(data, 'StreamingAssets');
  if (!fs.existsSync(streamingAssets)) return null;
  return {
    root: dir,
    streamingAssets,
    playground: path.join(streamingAssets, 'Playground'),
    managed: path.join(data, 'Managed'),
    executable: path.join(dir, 'Warudo.exe'),
  };
}

export function findWarudo({ explicit, steamRoots } = {}) {
  const direct = explicit || process.env.WARUDO_PATH;
  if (direct) return warudoLayout(direct);
  for (const library of steamLibraries({ steamRoots })) {
    const layout = warudoLayout(path.join(library, WARUDO_DIR));
    if (layout) return layout;
  }
  return null;
}
