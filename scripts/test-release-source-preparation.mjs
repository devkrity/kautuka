#!/usr/bin/env node

import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, readdir, rm, unlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const helper = path.join(root, 'scripts/prepare-release-source-snapshot.sh');
const keyPathResolver = path.join(root, 'scripts/resolve-release-key-path.sh');
const buildScript = await readFile(path.join(root, 'scripts/build-release-assets.sh'), 'utf8');
const workflow = await readFile(path.join(root, '.github/workflows/release-build-platform-assets.yml'), 'utf8');
const releasePipeline = await readFile(path.join(root, '.github/workflows/release-pipeline.yml'), 'utf8');
const tempRoot = await mkdtemp(path.join(os.tmpdir(), 'kautuka-release-source-'));

try {
  const repoRoot = path.join(tempRoot, 'repo');
  const sourceRoot = path.join(tempRoot, 'source');
  const stageDir = path.join(tempRoot, 'stage');
  const compilerContext = path.join(tempRoot, 'compiler-context');
  const invokingDirectory = tempRoot;
  const relativePrivateKey = 'repo/keys/manifest-signing-key.pem';
  const privateKey = path.join(repoRoot, 'keys/manifest-signing-key.pem');
  const cacheRoot = path.join(repoRoot, '.release-cache');
  await mkdir(repoRoot, { recursive: true });
  await mkdir(sourceRoot, { recursive: true });
  await mkdir(path.join(repoRoot, 'artifacts'), { recursive: true });
  await mkdir(path.join(repoRoot, 'publish'), { recursive: true });
  await mkdir(path.join(repoRoot, 'src/frontend/web/node_modules'), { recursive: true });
  await mkdir(path.dirname(privateKey), { recursive: true });
  await mkdir(stageDir, { recursive: true });
  await mkdir(cacheRoot, { recursive: true });
  await writeFile(path.join(repoRoot, 'LICENSE'), 'fixture license\n');
  await writeFile(path.join(repoRoot, 'NOTICE'), 'fixture notice\n');
  await writeFile(path.join(repoRoot, 'src.txt'), 'committed source version\n');
  await writeFile(path.join(repoRoot, 'removed.txt'), 'deleted from working tree\n');
  await writeFile(path.join(repoRoot, '.gitignore'), [
    '.env',
    '**/client-secret.txt',
    '*.db',
    '**/.venv/**',
    '**/.artifacts/**',
  ].join('\n') + '\n');
  execFileSync('git', ['init', '-q', '-b', 'main'], { cwd: repoRoot });
  execFileSync('git', ['config', 'user.name', 'Release Snapshot Test'], { cwd: repoRoot });
  execFileSync('git', ['config', 'user.email', 'release-snapshot-test@example.invalid'], { cwd: repoRoot });
  execFileSync('git', ['add', 'LICENSE', 'NOTICE', 'src.txt', 'removed.txt', '.gitignore'], { cwd: repoRoot });
  execFileSync('git', ['commit', '-q', '-m', 'fixture baseline'], { cwd: repoRoot });

  // Snapshotting must use the current tracked working tree, not HEAD alone.
  await writeFile(path.join(repoRoot, 'src.txt'), 'modified tracked working-tree source\n');
  await unlink(path.join(repoRoot, 'removed.txt'));
  await writeFile(privateKey, 'SENTINEL_PRIVATE_KEY_MUST_NOT_ENTER_SOURCE_SNAPSHOT\n');
  await writeFile(path.join(repoRoot, '.env'), 'IGNORED_ROOT_ENV_SENTINEL\n');
  await mkdir(path.join(repoRoot, 'src/backend/connectors/google-mail'), { recursive: true });
  await writeFile(path.join(repoRoot, 'src/backend/connectors/google-mail/client-secret.txt'), 'IGNORED_NESTED_SECRET_SENTINEL\n');
  await mkdir(path.join(repoRoot, 'data'), { recursive: true });
  await writeFile(path.join(repoRoot, 'data/session.db'), 'IGNORED_DATABASE_SENTINEL\n');
  await mkdir(path.join(repoRoot, '.venv/site-packages'), { recursive: true });
  await writeFile(path.join(repoRoot, '.venv/site-packages/private.txt'), 'IGNORED_VENV_SENTINEL\n');
  await mkdir(path.join(repoRoot, '.artifacts'), { recursive: true });
  await writeFile(path.join(repoRoot, '.artifacts/private.txt'), 'IGNORED_ARTIFACTS_SENTINEL\n');
  await mkdir(path.join(repoRoot, 'scripts'), { recursive: true });
  await writeFile(path.join(repoRoot, 'scripts/rehearsal-helper.sh'), '#!/bin/sh\necho intended-untracked-helper\n');
  await writeFile(path.join(repoRoot, 'artifacts/private-build.txt'), 'excluded artifact\n');
  await writeFile(path.join(repoRoot, 'publish/private-build.txt'), 'excluded publish file\n');
  await writeFile(path.join(repoRoot, 'src/frontend/web/node_modules/private.txt'), 'excluded dependency tree\n');
  await writeFile(path.join(cacheRoot, 'private-cache.txt'), 'excluded build cache\n');

  const oldOrder = spawnSync(process.execPath, [
    '-e',
    'require("node:fs").copyFileSync(process.argv[1], process.argv[2])',
    path.join(sourceRoot, 'LICENSE'),
    path.join(stageDir, 'LICENSE'),
  ], {
    encoding: 'utf8',
  });
  assert.notEqual(oldOrder.status, 0,
    'the former copy-before-snapshot order must fail against an empty temporary source root');
  assert.match(oldOrder.stderr, /ENOENT/u,
    'the former order must fail specifically because LICENSE is absent from the empty source snapshot');
  assert.match(oldOrder.stderr, /source[\\/]LICENSE/u,
    'the former order failure must identify the missing source LICENSE path');

  const normalizedPrivateKey = execFileSync('bash', [keyPathResolver, invokingDirectory, relativePrivateKey], { encoding: 'utf8' }).trim();
  assert.equal(normalizedPrivateKey, privateKey,
    'nested relative signing-key paths must resolve against the invocation directory before later directory changes');
  assert.match(buildScript, /private_key="\$\(bash "\$repo_root\/scripts\/resolve-release-key-path\.sh" "\$invocation_dir" "\$private_key"\)"/u,
    'the builder must normalize its private-key path before changing directories');
  assert.match(buildScript, /manifest_public_key="\$\(bash "\$repo_root\/scripts\/resolve-release-key-path\.sh" "\$invocation_dir" "\$manifest_public_key"\)"/u,
    'the builder must normalize its public-key path before changing directories');

  execFileSync('bash', [helper, repoRoot, sourceRoot, stageDir, normalizedPrivateKey, cacheRoot, compilerContext], { stdio: 'pipe' });

  assert.equal(await readFile(path.join(stageDir, 'LICENSE'), 'utf8'), 'fixture license\n');
  assert.equal(await readFile(path.join(stageDir, 'NOTICE'), 'utf8'), 'fixture notice\n');
  assert.equal(await readFile(path.join(sourceRoot, 'LICENSE'), 'utf8'), 'fixture license\n');
  assert.equal(await readFile(path.join(sourceRoot, 'NOTICE'), 'utf8'), 'fixture notice\n');
  assert.equal(await readFile(path.join(sourceRoot, 'src.txt'), 'utf8'), 'modified tracked working-tree source\n',
    'source snapshot must preserve modifications to tracked working-tree files');
  assert.equal(await readFile(path.join(sourceRoot, 'scripts/rehearsal-helper.sh'), 'utf8'), '#!/bin/sh\necho intended-untracked-helper\n',
    'source snapshot must include non-ignored untracked source helpers');

  for (const excluded of [
    '.git/HEAD',
    '.env',
    'src/backend/connectors/google-mail/client-secret.txt',
    'data/session.db',
    '.venv/site-packages/private.txt',
    '.artifacts/private.txt',
    'removed.txt',
    'artifacts/private-build.txt',
    'publish/private-build.txt',
    'src/frontend/web/node_modules/private.txt',
    '.release-cache/private-cache.txt',
    'keys/manifest-signing-key.pem',
  ]) {
    await assert.rejects(readFile(path.join(sourceRoot, excluded)), undefined,
      `source snapshot must exclude ${excluded}`);
  }

  async function collectFiles(directory) {
    const files = [];
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const entryPath = path.join(directory, entry.name);
      if (entry.isDirectory()) files.push(...await collectFiles(entryPath));
      else if (entry.isFile()) files.push(entryPath);
    }
    return files;
  }

  for (const file of await collectFiles(sourceRoot)) {
    assert.equal((await readFile(file, 'utf8')).includes('SENTINEL_PRIVATE_KEY_MUST_NOT_ENTER_SOURCE_SNAPSHOT'), false,
      'private-key sentinel must not be copied into source snapshot contents');
  }
  assert.deepEqual(await readdir(compilerContext), [],
    'compiler image context must remain empty and must not receive repository files or secrets');

  const contaminatedContext = path.join(tempRoot, 'contaminated-context');
  await mkdir(contaminatedContext, { recursive: true });
  await writeFile(path.join(contaminatedContext, 'unexpected.txt'), 'must be rejected\n');
  const contaminatedSnapshot = spawnSync('bash', [helper, repoRoot,
    path.join(tempRoot, 'rejected-source'), path.join(tempRoot, 'rejected-stage'), normalizedPrivateKey,
    cacheRoot, contaminatedContext], { encoding: 'utf8' });
  assert.equal(contaminatedSnapshot.status, 1,
    'source preparation must reject a compiler context that is not empty');
  assert.match(contaminatedSnapshot.stderr, /compiler image context must be an empty dedicated directory/u);

  const reusedSourceRoot = path.join(tempRoot, 'reused-source');
  const reusedSourceSentinel = path.join(reusedSourceRoot, 'stale-ignored-local-file');
  await mkdir(reusedSourceRoot, { recursive: true });
  await writeFile(reusedSourceSentinel, 'must not be silently retained or deleted\n');
  const reusedSourceContext = path.join(tempRoot, 'reused-source-context');
  await mkdir(reusedSourceContext, { recursive: true });
  const reusedSource = spawnSync('bash', [helper, repoRoot,
    reusedSourceRoot, path.join(tempRoot, 'reused-stage'), normalizedPrivateKey,
    cacheRoot, reusedSourceContext], { encoding: 'utf8' });
  assert.equal(reusedSource.status, 1,
    'source preparation must fail closed if its dedicated destination already has content');
  assert.match(reusedSource.stderr, /source snapshot destination must be an empty dedicated directory/u);
  assert.equal(await readFile(reusedSourceSentinel, 'utf8'), 'must not be silently retained or deleted\n',
    'the helper must preserve unexpected destination content when refusing reuse');

  const linuxBuildStart = workflow.indexOf('  build-linux-release-assets:\n');
  const windowsBuildStart = workflow.indexOf('  build-windows-installer-assets:\n');
  const linuxInstallerStart = workflow.indexOf('  build-linux-installer-assets:\n');
  assert.ok(linuxBuildStart >= 0 && windowsBuildStart > linuxBuildStart && linuxInstallerStart > windowsBuildStart,
    'release workflow must contain its three platform build jobs');
  const linuxBuildJob = workflow.slice(linuxBuildStart, windowsBuildStart);
  const windowsBuildJob = workflow.slice(windowsBuildStart, linuxInstallerStart);
  assert.match(linuxBuildJob, /node-version:\s*["']26["']/u,
    'Linux release build must use the supported Node 26 line');
  assert.match(linuxBuildJob, /product_version="\$\{MANIFEST_VERSION%%-preview\.\*\}"[\s\S]*?--product-version "\$product_version"/u,
    'Linux Preview builds must pass the base product version; the builder appends the channel version suffix itself');
  assert.match(windowsBuildJob, /node-version:\s*["']26["']/u,
    'Windows release build must use the supported Node 26 line');
  assert.match(windowsBuildJob, /-p:InformationalVersion="\$\{PRODUCT_VERSION\}\+\$\{SOURCE_SHA:0:7\}"[ \t]*\\\r?\n\s+-p:IncludeSourceRevisionInInformationalVersion=false/u,
    'Windows installer metadata must keep the explicit short SHA without the SDK appending it again');
  assert.match(workflow, /workflow_call:[\s\S]*?secrets:[\s\S]*?release_manifest_private_key:\s*\n\s+required:\s*true/u,
    'the reusable platform workflow must require the manifest signing key');
  assert.match(releasePipeline, /build-platform-assets:[\s\S]*?secrets:\s*\n\s+release_manifest_private_key:\s*\$\{\{\s*secrets\.RELEASE_MANIFEST_PRIVATE_KEY\s*\}\}/u,
    'the caller must pass its Preview or Stable environment signing key into the reusable platform workflow');
  assert.match(linuxBuildJob, /\$RUNNER_TEMP\/manifest-signing-key\.pem/u,
    'the Linux build job must keep its private signing key outside the checked-out workspace');
  assert.match(linuxBuildJob, /RELEASE_MANIFEST_PRIVATE_KEY_PATH=%s\\n.*\$GITHUB_ENV/u,
    'the Linux job must export the temporary key path without copying the secret into the workspace');
  assert.match(linuxBuildJob, /--private-key "\$RELEASE_MANIFEST_PRIVATE_KEY_PATH"/u,
    'the platform builder must use the temporary private-key path');
  assert.match(linuxBuildJob, /--manifest-public-key "\$RELEASE_MANIFEST_PUBLIC_KEY_PATH"/u,
    'the platform builder must use the corresponding public key path');
  assert.match(linuxBuildJob, /--channel "\$CHANNEL"/u,
    'the platform image build must retain channel identity without a channel-feed URL');
  assert.doesNotMatch(linuxBuildJob, /kautuka-release-feed-|--preview-manifest-url|--stable-manifest-url/u,
    'release appliances must discover GitHub Releases instead of depending on feed repositories');
  assert.match(linuxBuildJob, /https:\/\/github\.com\/\$\{GITHUB_REPOSITORY\}\/releases\/download\/\$\{RELEASE_TAG\}/u,
    'the signed release manifest must use exact-tag GitHub Release URLs');
  assert.doesNotMatch(workflow, /InstallerReleaseManifestUrl|--manifest-url.*releases\/download/u,
    'published installers must select the current channel release instead of pinning the build tag');
  assert.match(buildScript, /build_args\+=\("\$release_build_context"\)/u,
    'the compiler image build must use the dedicated empty context instead of the repository workspace');

  console.log('Release source preparation tests passed: current Git working-tree snapshot, ignored-file exclusion, non-ignored helper inclusion, empty destination/context guards, key exclusion, and GitHub Releases channel configuration.');
} finally {
  await rm(tempRoot, { recursive: true, force: true });
}
