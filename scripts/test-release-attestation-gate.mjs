#!/usr/bin/env node

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workflow = await readFile(path.join(root, '.github/workflows/release-build-platform-assets.yml'), 'utf8');

const inputMatch = workflow.match(/^      dry_run:\r?\n        required: true\r?\n        type: string\s*$/m);
assert.ok(inputMatch, 'the reusable workflow must receive a required dry_run string input');

const jobsOffset = workflow.indexOf('\njobs:\n');
assert.notEqual(jobsOffset, -1, 'the reusable workflow must contain jobs');
const jobs = workflow.slice(jobsOffset + '\njobs:\n'.length);
const jobStart = jobs.indexOf('  build-linux-installer-assets:\n');
assert.notEqual(jobStart, -1, 'the Linux installer build job must exist');
const jobTail = jobs.slice(jobStart);
const nextJob = jobTail.search(/\n  [a-zA-Z0-9_-]+:\r?\n/u);
const job = nextJob === -1 ? jobTail : jobTail.slice(0, nextJob);

function stepBlock(stepName) {
  const marker = `      - name: ${stepName}\n`;
  const start = job.indexOf(marker);
  assert.notEqual(start, -1, `${stepName} step must exist in the Linux installer job`);
  const tail = job.slice(start);
  const nextStep = tail.search(/\n      - name:/u);
  return nextStep === -1 ? tail : tail.slice(0, nextStep);
}

const attest = stepBlock('Attest Linux installer provenance');
assert.match(attest, /^        uses: actions\/attest@[0-9a-f]{40}\s+# v\d/mu,
  'installer provenance must use the pinned GitHub attestation action');
const condition = attest.match(/^        if: \$\{\{\s*(.*?)\s*\}\}\s*$/m)?.[1];
assert.equal(condition, "inputs.dry_run != 'true'",
  'GitHub attestation must be skipped only for an explicit dry run');
const shouldAttest = (dryRun) => dryRun !== 'true';
assert.equal(shouldAttest('true'), false, 'dry runs must not publish remote attestations');
assert.equal(shouldAttest('false'), true, 'real runs must attest the installer');
assert.doesNotMatch(attest, /^        continue-on-error:/m,
  'attestation failure must fail the job in real publication runs');

const upload = stepBlock('Upload Linux installer assets');
assert.match(upload, /^        uses: actions\/upload-artifact@[0-9a-f]{40}\s+# v\d/mu,
  'the generated installer must still be uploaded as a build artifact');
assert.doesNotMatch(upload, /^        if:/m,
  'the installer upload must retain GitHub Actions default success() gating after attestation');
assert.doesNotMatch(upload, /^        continue-on-error:/m,
  'a failed required attestation must not be converted into an uploaded successful build');
assert.ok(job.indexOf(attest) < job.indexOf(upload),
  'the attestation must run before uploading the installer artifact');

console.log('Release attestation gate tests passed: dry runs skip remote attestation; real runs require it before installer upload.');
