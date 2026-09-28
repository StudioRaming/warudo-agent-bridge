#!/usr/bin/env node
import { main } from '../src/cli.mjs';

const code = await main();
if (code !== null) process.exitCode = code;
