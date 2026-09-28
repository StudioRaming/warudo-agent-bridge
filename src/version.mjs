import fs from 'node:fs';

// package.json is the single source of the client version. The runtime reports
// its own version in the heartbeat; `doctor` and the tests compare the two.
export const VERSION = JSON.parse(fs.readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version;
