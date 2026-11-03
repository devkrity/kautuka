#!/usr/bin/env node
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const script = fileURLToPath(new URL('./release-source-policy.mjs', import.meta.url));
function run(mode, input, version) {
  return spawnSync(process.execPath, [script, mode, ...(version ? [version] : [])], { input: JSON.stringify(input), encoding: 'utf8' });
}
const checks = ['Format, Build, Test', 'Gitleaks', 'DCO'];
const rules = [
  { type: 'pull_request' },
  { type: 'non_fast_forward' },
  { type: 'required_status_checks', parameters: { required_status_checks: checks.map((context) => ({ context })) } },
];
assert.equal(run('protected-branch', rules).status, 0);
for (const missing of ['pull_request', 'non_fast_forward', 'required_status_checks']) {
  assert.notEqual(run('protected-branch', rules.filter((rule) => rule.type !== missing)).status, 0);
}
assert.notEqual(run('protected-branch', [rules[0], rules[1], { ...rules[2], parameters: { required_status_checks: [{ context: 'Gitleaks' }] } }]).status, 0);

const release = (tag, extra = {}) => ({ tag_name: tag, draft: false, prerelease: true, immutable: true, published_at: '2026-01-01T00:00:00Z', ...extra });
const releases = [[release('v0.1.20-preview.9'), release('v0.1.20-preview.10'), release('v0.1.21-preview.1', { draft: true })]];
assert.equal(run('latest-preview', releases).stdout, 'v0.1.20-preview.10');
assert.equal(run('latest-preview', [[], [release('v0.1.21-preview.1'), release('v0.1.20-preview.100')]]).stdout, 'v0.1.21-preview.1');
assert.equal(run('latest-preview-of-version', [[release('v0.1.21-preview.22'), release('v0.1.21-preview.23'), release('v0.1.22-preview.0')]], '0.1.21').stdout, 'v0.1.21-preview.23');
assert.notEqual(run('latest-preview-of-version', [[]], 'invalid').status, 0);
assert.equal(run('latest-preview', [[release('v0.1.20-preview.1', { prerelease: false })]]).stdout, '');
assert.notEqual(run('latest-preview', [[release('v0.1.21-preview.1', { immutable: false })]]).status, 0);
assert.notEqual(run('latest-preview', [[release('v0.1.21-preview.1'), release('v0.1.21-preview.1')]]).status, 0);
assert.notEqual(run('latest-preview', {}).status, 0);
console.log('Release source policy checks passed.');
