#!/usr/bin/env node

import { execFileSync } from 'node:child_process';

const [tag, sourceSha, dryRun = 'true'] = process.argv.slice(2);
const fail = (message) => {
  console.error(`error: ${message}`);
  process.exit(1);
};

if (!tag || !sourceSha) fail('usage: resolve-release.mjs <v* tag> <source sha> [dry_run]');
const preview = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-preview\.(0|[1-9]\d*)$/.exec(tag);
const stable = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(tag);
if (!preview && !stable) fail(`unsupported release tag '${tag}'; expected vX.Y.Z or vX.Y.Z-preview.N`);
const version = tag.slice(1);
const channel = preview ? 'preview' : 'stable';
if (!/^[0-9a-f]{40}$/i.test(sourceSha)) fail('source SHA must be a full 40-character commit SHA');

const output = { version, channel, release_tag: tag, source_sha: sourceSha.toLowerCase(), dry_run: dryRun === 'true' };
console.log(JSON.stringify(output, null, 2));

if (process.env.GITHUB_OUTPUT) {
  const fs = await import('node:fs');
  fs.appendFileSync(process.env.GITHUB_OUTPUT, Object.entries(output).map(([key, value]) => `${key}=${value}`).join('\n') + '\n');
}

if (process.env.GITHUB_STEP_SUMMARY) {
  const fs = await import('node:fs');
  fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `## Resolved release\n\n| Field | Value |\n|---|---|\n${Object.entries(output).map(([key, value]) => `| ${key} | \`${value}\` |`).join('\n')}\n`);
}

// When Git metadata is available, reject a tag whose commit is not on an allowed branch.
if (process.env.RELEASE_ELIGIBILITY_CHECK === 'true') {
  const branches = (process.env.RELEASE_ALLOWED_BRANCHES || 'origin/main').split(',').map((value) => value.trim()).filter(Boolean);
  const eligible = branches.some((branch) => {
    try {
      execFileSync('git', ['merge-base', '--is-ancestor', output.source_sha, branch], { stdio: 'ignore' });
      return true;
    } catch {
      return false;
    }
  });
  if (!eligible) fail(`tag commit ${output.source_sha} is not reachable from an allowed branch (${branches.join(', ')})`);
}
