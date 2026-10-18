import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';

// WARUDO compiles every Playground script as one assembly, so one call to an API that only the beta branch has stops the
// bridge, and every other Playground product, from loading on the stable branch (0.14.3, 2026-10-18: CharacterBoneLayer
// and CharacterAsset.GetBoneTransform, both 0.15 only). Such APIs are reached by reflection; this test keeps them out of
// direct code. Add a name here when another beta-only API turns up.
const BETA_ONLY = ['CharacterBoneLayer'];

const runtimeDir = new URL('../runtime/', import.meta.url);

function codeOnly(source) {
  // Drops comments and string literals, so a name may still be mentioned in text.
  return source
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/\/\/[^\n]*/g, ' ')
    .replace(/@"(?:[^"]|"")*"/g, '""')
    .replace(/"(?:[^"\\\n]|\\.)*"/g, '""');
}

test('runtime code does not call beta-only WARUDO APIs directly', () => {
  const hits = [];
  for (const name of fs.readdirSync(runtimeDir).filter((n) => n.endsWith('.cs'))) {
    const code = codeOnly(fs.readFileSync(new URL(name, runtimeDir), 'utf8'));
    for (const api of BETA_ONLY) if (new RegExp(`\\b${api}\\b`).test(code)) hits.push(`${name}: ${api}`);
  }
  assert.deepEqual(hits, []);
});

test('the shot bone fallback finds WARUDO\'s own lookup by reflection', () => {
  const code = fs.readFileSync(path.join(runtimeDir.pathname.replace(/^\/([A-Za-z]:)/, '$1'), 'AgentBridgePlugin.CharacterShot.cs'), 'utf8');
  assert.match(code, /static Transform AssetBone\(object asset, HumanBodyBones id\)/);
  assert.match(code, /"GetBoneTransform"/);
  assert.match(code, /Enum\.IsDefined\(ps\[1\]\.ParameterType, "Raw"\)/);
});
