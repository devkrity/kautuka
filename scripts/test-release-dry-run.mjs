#!/usr/bin/env node

import { createHash, generateKeyPairSync, sign as cryptoSign } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '..');
const sourceSha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
const tag = 'v0.1.20-preview.1';
const resolved = execFileSync(process.execPath, [path.join(root, 'scripts/resolve-release.mjs'), tag, sourceSha, 'true'], { cwd: root, encoding: 'utf8' });
process.stdout.write(`Nightly preview dry-run resolver (main HEAD ${sourceSha.slice(0, 7)}; previous preview tag v0.1.20-preview.0):\n`);
process.stdout.write(resolved);

const identity = JSON.parse(resolved);
if (identity.version !== tag.slice(1) || identity.channel !== 'preview' || identity.source_sha !== sourceSha || identity.dry_run !== true) {
  throw new Error('release resolver returned an unexpected identity');
}

const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'kautuka-release-dry-run-'));
try {
  const publishDir = path.join(directory, 'component');
  const project = path.join(root, 'src/backend/Kautuka.UpdateService/Kautuka.UpdateService.csproj');
  execFileSync('dotnet', [
    'publish', project, '--configuration', 'Release', '--output', publishDir,
    '-p:PublishAot=false', `-p:Version=${identity.version}`,
    `-p:InformationalVersion=${identity.version}+${sourceSha.slice(0, 7)}`,
    '-p:AssemblyVersion=0.1.20.0', '-p:FileVersion=0.1.20.0',
  ], { cwd: root, stdio: 'inherit' });
  const frontendDist = path.join(directory, 'frontend-dist');
  const frontendEnv = { ...process.env, VITE_KAUTUKA_VERSION: identity.version, VITE_KAUTUKA_SOURCE_SHA: sourceSha.slice(0, 7) };
  if (process.platform === 'win32') {
    execFileSync('powershell.exe', ['-NoProfile', '-Command', `pnpm --dir src/frontend/web exec vite build --outDir '${frontendDist.replaceAll("'", "''")}'`], { cwd: root, stdio: 'inherit', env: frontendEnv });
  } else {
    execFileSync('pnpm', ['--dir', 'src/frontend/web', 'exec', 'vite', 'build', '--outDir', frontendDist], { cwd: root, stdio: 'inherit', env: frontendEnv });
  }
  const dllPath = path.join(publishDir, 'Kautuka.UpdateService.dll');
  const archiveName = `kautuka-update-service-${identity.version}-win-x64.zip`;
  const archivePath = path.join(directory, archiveName);
  const ps = `$ErrorActionPreference='Stop'; Compress-Archive -Path '${publishDir.replaceAll("'", "''")}\\*' -DestinationPath '${archivePath.replaceAll("'", "''")}'`;
  execFileSync('powershell.exe', ['-NoProfile', '-Command', ps], { cwd: root, stdio: 'inherit' });

  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const payload = await fs.readFile(archivePath);
  const linuxName = 'kautuka-linux-amd64.tar.gz';
  const wslName = 'kautuka-wsl-appliance.tar.gz';
  const artifact = (name) => ({ path: name, sha256: createHash('sha256').update(payload).digest('hex') });
  const manifestBytes = Buffer.from(`${JSON.stringify({ version: identity.version, channel: identity.channel, sourceSha: identity.source_sha, artifacts: [artifact(linuxName), artifact(wslName)] }, null, 2)}\n`);
  await fs.writeFile(path.join(directory, linuxName), payload);
  await fs.writeFile(path.join(directory, wslName), payload);
  await fs.writeFile(path.join(directory, archiveName), payload);
  await fs.writeFile(path.join(directory, 'kautuka-platform-release.json'), manifestBytes);
  await fs.writeFile(path.join(directory, 'kautuka-platform-release.sig'), `${cryptoSign('sha256', manifestBytes, privateKey).toString('base64')}\n`);
  const publicKeyPath = path.join(directory, 'public.pem');
  await fs.writeFile(publicKeyPath, publicKey.export({ type: 'spki', format: 'pem' }));

  const check = execFileSync(process.execPath, [
    path.join(root, 'scripts/verify-release-artifacts.mjs'),
    '--directory', directory, '--version', identity.version, '--channel', identity.channel,
    '--source-sha', identity.source_sha, '--public-key', publicKeyPath,
    '--dotnet-binary', dllPath, '--expected-files', `${archiveName},${linuxName},${wslName}`,
    '--frontend-bundle', path.join(frontendDist, 'assets'),
    '--container-tag', `ghcr.io/devkrity/kautuka-base:${identity.version}`,
  ], { cwd: root, encoding: 'utf8' });
  process.stdout.write(check);
  console.log(`Real component platform asset built and checked: ${archiveName}; container tag identity checked without pushing.`);
} finally {
  await fs.rm(directory, { recursive: true, force: true });
}
