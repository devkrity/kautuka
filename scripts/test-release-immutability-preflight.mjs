#!/usr/bin/env node
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile, chmod, readFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const helper = path.join(root, 'scripts/require-github-release-immutability.sh');
const workflow = path.join(root, '.github/workflows/release-publish-assets.yml');
const temp = await mkdtemp(path.join(os.tmpdir(), 'kautuka-immutability-preflight-'));

try {
  const bin = path.join(temp, 'bin');
  await mkdir(bin);
  const fakeGh = path.join(bin, 'gh');
  await writeFile(fakeGh, `#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${'${MOCK_GH_LOG:?}' }"
case "${'${MOCK_IMMUTABILITY:-unknown}'}" in
  enabled) [[ "$*" == *'immutable-releases'* ]] && printf 'true\\n' ;;
  disabled) [[ "$*" == *'immutable-releases'* ]] && printf 'false\\n' ;;
  unknown) echo 'simulated API error' >&2; exit 1 ;;
  *) exit 2 ;;
esac
`);
  await chmod(fakeGh, 0o755);

  const log = path.join(temp, 'gh-calls.log');
  const env = { ...process.env, PATH: `${bin}${path.delimiter}${process.env.PATH ?? ''}`, MOCK_GH_LOG: log };
  const enabled = spawnSync('bash', [helper, 'devkrity/kautuka'], { env: { ...env, MOCK_IMMUTABILITY: 'enabled' }, encoding: 'utf8' });
  assert.equal(enabled.status, 0, enabled.stderr || 'enabled immutability must pass');
  assert.match(enabled.stdout, /immutability is enabled/u);
  assert.match(await readFile(log, 'utf8'), /X-GitHub-Api-Version: 2026-03-10/u);

  for (const state of ['disabled', 'unknown']) {
    await writeFile(log, '');
    const result = spawnSync('bash', [helper, 'devkrity/kautuka'], { env: { ...env, MOCK_IMMUTABILITY: state }, encoding: 'utf8' });
    assert.notEqual(result.status, 0, `${state} immutability state must block publication`);
    assert.match(result.stderr, /keep release publication disabled/u);
    assert.match(await readFile(log, 'utf8'), /repos\/devkrity\/kautuka\/immutable-releases/u);
  }

  const workflowText = await readFile(workflow, 'utf8');
  const publishJob = workflowText.slice(workflowText.indexOf('  publish-github-release:\n'));
  assert.doesNotMatch(publishJob, /require-github-release-immutability\.sh/u,
    'the operator-only check must not run in CI with a contents-only token');
  const publishIndex = publishJob.indexOf('Publish immutable GitHub Release');
  const postCheckIndex = publishJob.indexOf('Require GitHub immutable-release enforcement');
  const pointerCheckoutIndex = publishJob.indexOf('Check out the protected release-channel branch');
  const draftMutationIndex = publishJob.indexOf('Confirm release draft is empty and immutable-release compatible');
  const assetUploadIndex = publishJob.indexOf('Upload all versioned release assets to the GitHub draft');
  const pointerPublishIndex = publishJob.indexOf('Advance only the selected signed channel pointer');
  assert.ok(publishIndex >= 0 && publishIndex < postCheckIndex,
    'the workflow must retain the post-publication immutable assertion');
  assert.ok(pointerCheckoutIndex >= 0 && pointerCheckoutIndex < draftMutationIndex && pointerCheckoutIndex < assetUploadIndex && pointerCheckoutIndex < publishIndex,
    'the release-channel branch must be checked out before draft edits, asset uploads, or publication');
  assert.ok(publishIndex < postCheckIndex && postCheckIndex < pointerPublishIndex,
    'the channel pointer may advance only after immutable-release enforcement is confirmed');
  assert.match(publishJob, /node scripts\/update-release-channel-pointer\.mjs/u);
  assert.match(publishJob, /git -C release-channel add "\$CHANNEL\.json"/u,
    'publisher must stage only its input channel pointer');
  assert.doesNotMatch(publishJob, /git -C release-channel add -A|git -C release-channel add \./u,
    'publisher must not stage the other channel pointer');
  assert.doesNotMatch(publishJob, /release_feed_deploy_key|raw\.githubusercontent\.com\/devkrity\/kautuka-release-feed/u,
    'publisher must not depend on separate feed repositories or deploy keys');
  console.log('Operator immutability-preflight CLI tests passed: enabled succeeds; disabled or unknown settings fail closed. CI retains its post-publication immutable assertion and advances only the selected signed pointer afterward.');
} finally {
  await rm(temp, { recursive: true, force: true });
}
