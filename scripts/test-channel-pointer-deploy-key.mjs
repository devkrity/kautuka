#!/usr/bin/env node
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtemp, readFile, rm, stat, writeFile, chmod, mkdir } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const helper = path.join(root, 'scripts/publish-channel-pointer.sh');
const workflow = path.join(root, '.github/workflows/release-publish-assets.yml');
const temp = await mkdtemp(path.join(os.tmpdir(), 'kautuka-feed-deploy-key-test-'));

function result(args, env) {
  return spawnSync('bash', [helper, ...args], { cwd: root, env, encoding: 'utf8' });
}

try {
  const runnerTemp = path.join(temp, 'runner-temp');
  const fakeBin = path.join(temp, 'bin');
  await mkdir(runnerTemp);
  await mkdir(fakeBin);
  const keyPath = path.join(temp, 'test-deploy-key');
  execFileSync('ssh-keygen', ['-q', '-t', 'ed25519', '-N', '', '-C', 'offline-test-only', '-f', keyPath], { stdio: 'ignore' });
  const key = await readFile(keyPath, 'utf8');
  const keyFingerprint = execFileSync('ssh-keygen', ['-lf', path.join(root, 'scripts/github-known-hosts')], { encoding: 'utf8' });
  assert.match(keyFingerprint, /SHA256:\+DiY3wvvV6TuJJhbpZisF\/zLDA0zPMSvHdkr4UvCOqU/u, 'pinned known_hosts key must match GitHub’s published ED25519 fingerprint');

  const fakeGit = `#!/usr/bin/env bash
set -euo pipefail
log="${'${MOCK_GIT_LOG:?}'}"
cmd=()
if [[ "${'${1:-}'}" == '-C' ]]; then
  cmd=("$@"); shift 2
else
  cmd=("$@")
fi
printf '%s\\n' "${'${cmd[*]}'}" >> "$log"
ssh_command="${'${GIT_SSH_COMMAND:-}'}"
[[ "$ssh_command" == *'IdentitiesOnly=yes'* && "$ssh_command" == *'BatchMode=yes'* && "$ssh_command" == *'ConnectTimeout=10'* && "$ssh_command" == *'ServerAliveCountMax=2'* && "$ssh_command" == *'StrictHostKeyChecking=yes'* && "$ssh_command" == *'GlobalKnownHostsFile=/dev/null'* && "$ssh_command" == *'UpdateHostKeys=no'* ]] || { echo 'SSH policy missing or unbounded' >&2; exit 91; }
key_file="${'${ssh_command#*-i }'}"; key_file="${'${key_file%% *}'}"
[[ -f "$key_file" && "$(stat -c '%a' "$key_file")" == '600' ]] || { echo 'temporary key file missing or insecure' >&2; exit 92; }
printf 'key_file=%s\\n' "$key_file" >> "$log"
if [[ "${'${1:-}'}" == '-C' ]]; then shift 2; fi
case "${'${1:-}'}" in
  ls-remote) printf '0123456789012345678901234567890123456789\\trefs/heads/main\\n' ;;
  push)
    if [[ "${'${2:-}'}" == '--dry-run' ]]; then
      [[ "${'${MOCK_FAIL_PREFLIGHT:-0}'}" == '1' ]] && { echo 'permission denied' >&2; exit 1; }
      printf 'dry-run-success\\n' >> "$log"
    else
      printf 'real-pointer-push\\n' >> "$log"
      if [[ "${'${MOCK_FAIL_POINTER_PUSH:-0}'}" == '1' ]]; then exit 1; fi
    fi
    ;;
  clone)
    destination="${'${@: -1}'}"
    mkdir -p "$destination/.git"
    ;;
  config|add|commit) ;;
  diff)
    [[ "${'${MOCK_NO_CHANGE:-0}'}" == '1' ]] && exit 0
    exit 1
    ;;
  *) echo "unexpected git command" >&2; exit 93 ;;
esac
`;
  const fakeGitPath = path.join(fakeBin, 'git');
  await writeFile(fakeGitPath, fakeGit, { mode: 0o755 });
  await chmod(fakeGitPath, 0o755);

  const logPath = path.join(temp, 'git-calls.log');
  const baseEnv = {
    ...process.env,
    PATH: `${fakeBin}${path.delimiter}${process.env.PATH ?? ''}`,
    RUNNER_TEMP: runnerTemp,
    GITHUB_RUN_ID: '1234',
    GITHUB_RUN_ATTEMPT: '1',
    MOCK_GIT_LOG: logPath,
  };

  const missing = result(['preflight', '--channel', 'preview', '--source-sha', '0123456789012345678901234567890123456789'], { ...baseEnv, RELEASE_FEED_DEPLOY_KEY: '' });
  assert.notEqual(missing.status, 0, 'missing deploy-key secret must fail');
  assert.match(missing.stderr, /release_feed_deploy_key/u);
  await writeFile(logPath, '');

  const malformed = result(['preflight', '--channel', 'preview', '--source-sha', '0123456789012345678901234567890123456789'], { ...baseEnv, RELEASE_FEED_DEPLOY_KEY: 'not a private key' });
  assert.notEqual(malformed.status, 0, 'invalid private-key material must fail before Git network access');
  assert.match(malformed.stderr, /not a valid unencrypted/u);
  assert.equal((await readFile(logPath, 'utf8')).trim(), '', 'invalid key must fail before any Git request');

  const simulatedDeniedKey = result(['preflight', '--channel', 'preview', '--source-sha', '0123456789012345678901234567890123456789'], { ...baseEnv, RELEASE_FEED_DEPLOY_KEY: key, MOCK_FAIL_PREFLIGHT: '1' });
  assert.notEqual(simulatedDeniedKey.status, 0, 'simulated remote write denial must fail preflight');
  assert.match(simulatedDeniedKey.stderr, /write-permission preflight/u);
  const deniedCalls = await readFile(logPath, 'utf8');
  assert.match(deniedCalls, /ls-remote/u);
  assert.match(deniedCalls, /--dry-run/u);
  assert.doesNotMatch(deniedCalls, /clone|real-pointer-push/u, 'failed preflight must not clone or write the pointer');
  const deniedKeyPath = deniedCalls.match(/^key_file=(.+)$/mu)?.[1];
  assert.ok(deniedKeyPath, 'Git fake must observe a temporary key file');
  await assert.rejects(stat(deniedKeyPath), 'temporary key file must be removed after failed preflight');

  await writeFile(logPath, '');
  const preflight = result(['preflight', '--channel', 'preview', '--source-sha', '0123456789012345678901234567890123456789'], { ...baseEnv, RELEASE_FEED_DEPLOY_KEY: key });
  assert.equal(preflight.status, 0, preflight.stderr || 'valid deploy key preflight failed');
  const preflightCalls = await readFile(logPath, 'utf8');
  assert.match(preflightCalls, /--dry-run/u);
  assert.match(preflightCalls, /dry-run-success/u);
  assert.doesNotMatch(preflightCalls, /real-pointer-push/u, 'preflight must not publish a branch');
  const preflightKeyPath = preflightCalls.match(/^key_file=(.+)$/mu)?.[1];
  assert.ok(preflightKeyPath);
  await assert.rejects(stat(preflightKeyPath), 'temporary key file must be removed after successful preflight');

  await writeFile(logPath, '');
  const preflightWithoutTrailingNewline = result(['preflight', '--channel', 'preview', '--source-sha', '0123456789012345678901234567890123456789'], { ...baseEnv, RELEASE_FEED_DEPLOY_KEY: key.replace(/\n+$/u, '') });
  assert.equal(preflightWithoutTrailingNewline.status, 0, preflightWithoutTrailingNewline.stderr || 'valid key without a trailing newline must pass');
  const trimmedKeyPath = (await readFile(logPath, 'utf8')).match(/^key_file=(.+)$/mu)?.[1];
  assert.ok(trimmedKeyPath);
  await assert.rejects(stat(trimmedKeyPath), 'temporary key file must be removed after newline-normalized preflight');

  await writeFile(logPath, '');
  const manifestPath = path.join(temp, 'manifest.json');
  await writeFile(manifestPath, JSON.stringify({
    channel: 'preview',
    releaseTag: 'v0.1.20-preview.1',
    sourceSha: '0123456789012345678901234567890123456789',
    signature: { algorithm: 'ecdsa-p256-sha256', url: 'https://github.com/devkrity/kautuka/releases/download/v0.1.20-preview.1/kautuka-platform-release.sig' },
    artifacts: [{ path: 'kautuka-linux-amd64.tar.gz' }],
  }));
  const publish = result(['publish', '--channel', 'preview', '--release-tag', 'v0.1.20-preview.1', '--source-sha', '0123456789012345678901234567890123456789', '--manifest', manifestPath], { ...baseEnv, RELEASE_FEED_DEPLOY_KEY: key });
  assert.equal(publish.status, 0, 'status=' + publish.status + ' error=' + (publish.error?.message ?? '') + ' stdout=' + (publish.stdout ?? '') + ' stderr=' + (publish.stderr ?? ''));
  const publishCalls = await readFile(logPath, 'utf8');
  const cloneIndex = publishCalls.indexOf('clone');
  const addIndex = publishCalls.indexOf('add -- manifest');
  const commitIndex = publishCalls.indexOf('commit');
  const pushIndex = publishCalls.indexOf('push origin HEAD:refs/heads/main');
  assert.ok(cloneIndex >= 0 && addIndex > cloneIndex && commitIndex > addIndex && pushIndex > commitIndex, 'pointer change must clone, stage manifest, commit, and use a normal fast-forward push in order');
  assert.doesNotMatch(publishCalls, /--force/u, 'pointer publisher must not force-push');
  const publishKeyPath = publishCalls.match(/^key_file=(.+)$/mu)?.[1];
  assert.ok(publishKeyPath);
  await assert.rejects(stat(publishKeyPath), 'temporary key file must be removed after simulated publication');

  await writeFile(logPath, '');
  const rejectedPush = result(['publish', '--channel', 'preview', '--release-tag', 'v0.1.20-preview.1', '--source-sha', '0123456789012345678901234567890123456789', '--manifest', manifestPath], { ...baseEnv, RELEASE_FEED_DEPLOY_KEY: key, MOCK_FAIL_POINTER_PUSH: '1' });
  assert.notEqual(rejectedPush.status, 0, 'a rejected pointer push must fail the publisher');
  const rejectedPushCalls = await readFile(logPath, 'utf8');
  assert.match(rejectedPushCalls, /real-pointer-push/u, 'the simulated remote must reject the attempted normal pointer push');
  assert.doesNotMatch(rejectedPushCalls, /--force/u, 'rejected pointer updates must not trigger a force push');
  const rejectedKeyPath = rejectedPushCalls.match(/^key_file=(.+)$/mu)?.[1];
  const rejectedWorkDir = rejectedPushCalls.match(/^clone .* ([^\r\n]+)$/mu)?.[1];
  assert.ok(rejectedKeyPath && rejectedWorkDir);
  await assert.rejects(stat(rejectedKeyPath), 'temporary key file must be removed after rejected pointer push');
  await assert.rejects(stat(rejectedWorkDir), 'temporary clone must be removed after rejected pointer push');

  await writeFile(logPath, '');
  const noOp = result(['publish', '--channel', 'preview', '--release-tag', 'v0.1.20-preview.1', '--source-sha', '0123456789012345678901234567890123456789', '--manifest', manifestPath], { ...baseEnv, RELEASE_FEED_DEPLOY_KEY: key, MOCK_NO_CHANGE: '1' });
  assert.notEqual(noOp.status, 0, 'an unchanged pointer must fail rather than create a no-op commit');
  assert.match(noOp.stderr, /already contains this manifest/u);
  const noOpCalls = await readFile(logPath, 'utf8');
  assert.doesNotMatch(noOpCalls, /real-pointer-push/u, 'no-op pointer publication must stop before push');
  const noOpKeyPath = noOpCalls.match(/^key_file=(.+)$/mu)?.[1];
  const noOpWorkDir = noOpCalls.match(/^clone .* ([^\r\n]+)$/mu)?.[1];
  assert.ok(noOpKeyPath && noOpWorkDir);
  await assert.rejects(stat(noOpKeyPath), 'temporary key file must be removed after no-op rejection');
  await assert.rejects(stat(noOpWorkDir), 'temporary clone must be removed after no-op rejection');

  const wrongIdentity = JSON.parse(await readFile(manifestPath, 'utf8'));
  wrongIdentity.sourceSha = 'ffffffffffffffffffffffffffffffffffffffff';
  await writeFile(manifestPath, JSON.stringify(wrongIdentity));
  await writeFile(logPath, '');
  const mismatch = result(['publish', '--channel', 'preview', '--release-tag', 'v0.1.20-preview.1', '--source-sha', '0123456789012345678901234567890123456789', '--manifest', manifestPath], { ...baseEnv, RELEASE_FEED_DEPLOY_KEY: key });
  assert.notEqual(mismatch.status, 0, 'pointer publisher must reject a manifest with mismatched source SHA');
  assert.match(mismatch.stderr, /identity does not match/u);
  assert.equal((await readFile(logPath, 'utf8')).trim(), '', 'identity mismatch must fail before Git access');

  const workflowText = await readFile(workflow, 'utf8');
  const feedJob = workflowText.slice(workflowText.indexOf('  publish-public-feed:\n'));
  const names = [
    'Preflight channel pointer write access',
    'Upload all versioned release assets to the GitHub draft',
    'Publish immutable GitHub Release',
    'Move the GitHub-hosted channel pointer after release publication',
  ].map((name) => feedJob.indexOf(`- name: ${name}`));
  assert.ok(names.every((index) => index >= 0), 'release workflow must contain preflight, asset upload, immutable release, and pointer steps');
  assert.ok(names[0] < names[1] && names[1] < names[2] && names[2] < names[3], 'deploy-key preflight must precede asset upload, and pointer commit must follow immutable release publication');
  assert.doesNotMatch(feedJob, /release_feed_token/u, 'publisher must not depend on a broad PAT secret');

  console.log('Channel pointer deploy-key tests passed: missing/malformed key rejection, simulated remote permission denial, 0600 temp key cleanup on success and failure, strict pinned host policy, dry-run preflight, manifest identity binding, pointer push rejection/no-op cleanup, no-force push, and release/pointer ordering.');
} finally {
  await rm(temp, { recursive: true, force: true });
}
