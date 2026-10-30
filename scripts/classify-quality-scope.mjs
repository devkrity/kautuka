import { execFileSync } from 'node:child_process';
import { appendFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { classifyQualityScope } from './lib/quality-scope.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const eventName = process.env.GITHUB_EVENT_NAME ?? '';
let scope = 'full';
let changedFiles = [];

if (eventName === 'pull_request') {
  const baseSha = process.env.BASE_SHA ?? '';
  const headSha = process.env.GITHUB_SHA ?? '';
  if (!/^[a-f0-9]{40}$/i.test(baseSha) || !/^[a-f0-9]{40}$/i.test(headSha)) {
    throw new Error('Pull request base/head SHA is missing or malformed; failing closed to prevent partial validation.');
  }

  const diff = execFileSync(
    'git',
    ['diff', '--no-renames', '--name-only', '-z', baseSha, headSha],
    { cwd: root, encoding: 'utf8' },
  );
  changedFiles = diff.split('\0').filter(Boolean);
  scope = classifyQualityScope(changedFiles);
}

const output = process.env.GITHUB_OUTPUT;
if (!output) throw new Error('GITHUB_OUTPUT is required.');
await appendFile(output, `scope=${scope}\nchanged_file_count=${changedFiles.length}\n`);
console.log(`Quality scope: ${scope} (${changedFiles.length} changed files).`);
