#!/usr/bin/env node
// Runs the restore bookkeeping of warudo_ui_language (runtime/LanguageRestore.cs) under plain .NET. It uses no Unity or WARUDO types,
// so only a .NET SDK is needed. Exit code 2 means the environment has no .NET SDK.
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { finish, runCSharp } from './lib/run-csharp.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
finish(runCSharp({
  name: 'languagerestore',
  sources: [path.join(root, 'scripts', 'language-restore', 'Program.cs'), path.join(root, 'runtime', 'LanguageRestore.cs')],
}));
