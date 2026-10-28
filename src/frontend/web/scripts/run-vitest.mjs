import { mkdir } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const projectDir = resolve(fileURLToPath(new URL('..', import.meta.url)));
const tempDir = resolve(projectDir, '.tmp-vitest');
await mkdir(tempDir, { recursive: true });

// Vitest/Vite create temporary transform files. In restricted Windows environments,
// the OS temp folder may be inaccessible to the test process. Keep those files in
// the ignored project-local temp directory so the normal test command is reliable.
process.env.TEMP = tempDir;
process.env.TMP = tempDir;

const vitestCli = resolve(projectDir, 'node_modules/vitest/vitest.mjs');
const child = spawn(process.execPath, [vitestCli, ...process.argv.slice(2)], {
  cwd: projectDir,
  env: process.env,
  stdio: 'inherit',
});

child.on('error', (error) => {
  console.error('Unable to start Vitest:', error);
  process.exitCode = 1;
});

child.on('exit', (code, signal) => {
  if (signal) {
    console.error(`Vitest exited after receiving ${signal}.`);
    process.exitCode = 1;
    return;
  }
  process.exitCode = code ?? 1;
});
