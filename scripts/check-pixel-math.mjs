#!/usr/bin/env node
// Runs the pixel math of the capture tools (runtime/Pixmap.cs, runtime/PixelDiff.cs: crop, box-filter downscale, diff boxes and
// merge gap, color statistics) under plain .NET, next to a stub of the few UnityEngine types they touch (scripts/pixel-math/Stub.cs).
// Needs a .NET SDK and WARUDO's Newtonsoft.Json.dll (same requirements as scripts/compile-runtime.mjs). No WARUDO is started.
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { finish, runCSharp } from './lib/run-csharp.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
finish(runCSharp({
  name: 'pixelmath',
  newtonsoft: true,
  sources: [path.join(root, 'scripts', 'pixel-math', 'Stub.cs'), path.join(root, 'scripts', 'pixel-math', 'Program.cs'), path.join(root, 'runtime', 'Pixmap.cs'), path.join(root, 'runtime', 'PixelDiff.cs')],
}));
