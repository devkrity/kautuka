#!/usr/bin/env node
/**
 * Root ratchet: production frontend must not retain AG-UI, CopilotKit, or assistant-ui packages.
 */
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const frontendRatchet = path.join(repoRoot, 'src/frontend/scripts/validate-no-legacy-runtime-packages.mjs');

const result = spawnSync(process.execPath, [frontendRatchet], {
  cwd: repoRoot,
  stdio: 'inherit',
});

if (result.status !== 0) {
  process.exit(result.status ?? 1);
}

console.log('Root legacy runtime package ratchet passed.');
