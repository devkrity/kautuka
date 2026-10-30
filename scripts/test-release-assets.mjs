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
const required = JSON.parse(await readFile(path.join(scriptDir, 'release-public-assets.json'), 'utf8'));

try {
  for (const name of required) await writeFile(path.join(releaseDir, name), `fixture:${name}\n`);
  execFileSync(process.execPath, [
    path.join(scriptDir, 'generate-release-asset-manifest.mjs'),
    '--directory', releaseDir,
    '--relative-prefix', `releases/${tag}`,
  ], { cwd: root, stdio: 'inherit' });
  execFileSync(process.execPath, [path.join(scriptDir, 'verify-release-assets.mjs'), '--directory', releaseDir, '--tag', tag], { cwd: root, stdio: 'inherit' });

  await writeFile(path.join(releaseDir, 'kautuka-release-support.zip'), 'tampered after checksums');
  const tampered = spawnSync(process.execPath, [path.join(scriptDir, 'verify-release-assets.mjs'), '--directory', releaseDir, '--tag', tag], { cwd: root, encoding: 'utf8' });
  if (tampered.status === 0) throw new Error('asset verifier accepted a changed release asset');
  await writeFile(path.join(releaseDir, 'kautuka-release-support.zip'), 'fixture:kautuka-release-support.zip\n');
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
