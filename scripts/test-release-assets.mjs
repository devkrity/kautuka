#!/usr/bin/env node
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptDir, '..');
const tag = 'v0.1.20-preview.1';
const releaseDir = await mkdtemp(path.join(os.tmpdir(), 'kautuka-flat-release-assets-'));
const required = [
  'kautuka-install-linux.sh', 'Kautuka.Installer.exe', 'kautuka-linux-amd64.tar.gz',
  'kautuka-wsl-appliance.tar.gz', 'kautuka-platform-release.json', 'kautuka-platform-release.sig',
  'LICENSE', 'NOTICE', 'THIRD_PARTY_NOTICES.txt', 'THIRD_PARTY_COMPONENTS.json',
  'FRONTEND_DEPENDENCIES.json',
  'CREDITS.md', 'kautuka-licenses.zip', 'kautuka-third-party-sources.zip',
  'kautuka-linux-amd64.spdx.json', 'kautuka-wsl-appliance.spdx.json',
];

try {
  for (const name of required) await writeFile(path.join(releaseDir, name), `fixture:${name}\n`);
  execFileSync(process.execPath, [
    path.join(scriptDir, 'generate-release-asset-manifest.mjs'),
    '--directory', releaseDir,
    '--relative-prefix', `releases/${tag}`,
  ], { cwd: root, stdio: 'inherit' });
  execFileSync(process.execPath, [path.join(scriptDir, 'verify-release-assets.mjs'), '--directory', releaseDir, '--tag', tag], { cwd: root, stdio: 'inherit' });

  await writeFile(path.join(releaseDir, 'kautuka-licenses.zip'), 'tampered after checksums');
  const tampered = spawnSync(process.execPath, [path.join(scriptDir, 'verify-release-assets.mjs'), '--directory', releaseDir, '--tag', tag], { cwd: root, encoding: 'utf8' });
  if (tampered.status === 0) throw new Error('asset verifier accepted a changed release asset');
  await writeFile(path.join(releaseDir, 'kautuka-licenses.zip'), 'fixture:kautuka-licenses.zip\n');
  execFileSync(process.execPath, [path.join(scriptDir, 'generate-release-asset-manifest.mjs'), '--directory', releaseDir, '--relative-prefix', `releases/${tag}`], { cwd: root, stdio: 'ignore' });
  await mkdir(path.join(releaseDir, 'licenses'));
  const nested = spawnSync(process.execPath, [path.join(scriptDir, 'verify-release-assets.mjs'), '--directory', releaseDir, '--tag', tag], { cwd: root, encoding: 'utf8' });
  if (nested.status === 0) throw new Error('asset verifier accepted a nested directory instead of a flat release asset set');

  await rm(path.join(releaseDir, 'licenses'), { recursive: true, force: true });
  const manifestPath = path.join(releaseDir, 'kautuka-release-assets.json');
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  manifest.assets[0].sizeBytes = 2 * 1024 * 1024 * 1024;
  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  const oversized = spawnSync(process.execPath, [path.join(scriptDir, 'verify-release-assets.mjs'), '--directory', releaseDir, '--tag', tag], { cwd: root, encoding: 'utf8' });
  if (oversized.status === 0 || !`${oversized.stderr}${oversized.stdout}`.includes("GitHub's 2 GiB per-file limit")) {
    throw new Error('asset verifier did not reject a declared asset at GitHub’s 2 GiB per-file limit');
  }

  console.log('release asset validation rejected changed, non-flat, and oversized asset sets');
} finally {
  await rm(releaseDir, { recursive: true, force: true });
}
