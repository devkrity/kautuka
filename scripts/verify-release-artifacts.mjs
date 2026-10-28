#!/usr/bin/env node

import { createHash, createPublicKey, verify as cryptoVerify } from 'node:crypto';
import { promises as fs } from 'node:fs';
import { execFileSync } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';

function parseArgs(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index += 2) result[argv[index].slice(2)] = argv[index + 1];
  return result;
}

const options = parseArgs(process.argv.slice(2));
const fail = (message) => {
  console.error(`error: ${message}`);
  process.exit(1);
};

if (!options.directory || !options.version || !options.channel || !options['source-sha'] || !options['public-key']) {
  fail('usage: verify-release-artifacts.mjs --directory DIR --version VERSION --channel preview|stable --source-sha SHA --public-key PEM');
}

const expectedFileNames = (options['expected-files'] ?? '').split(',').filter(Boolean).sort();
if (expectedFileNames.length) {
  const actualFileNames = (await fs.readdir(options.directory, { recursive: true }))
    .filter((entry) => !entry.includes(path.sep))
    .sort();
  for (const file of expectedFileNames) if (!actualFileNames.includes(file)) fail(`expected artifact file name missing: ${file}`);
}

if (options['dotnet-binary']) {
  const binary = await fs.readFile(options['dotnet-binary']);
  const informationalVersion = `${options.version}+${options['source-sha'].slice(0, 7)}`;
  if (!binary.includes(Buffer.from(informationalVersion))) fail(`.NET binary does not contain InformationalVersion ${informationalVersion}`);
}

if (options['frontend-bundle']) {
  const files = await fs.readdir(options['frontend-bundle']);
  const bundles = await Promise.all(files.filter((file) => file.endsWith('.js')).map((file) => fs.readFile(path.join(options['frontend-bundle'], file))));
  const bundleText = Buffer.concat(bundles).toString('utf8');
  for (const value of [options.version, options['source-sha'].slice(0, 7)]) {
    if (!bundleText.includes(value)) fail(`frontend bundle does not contain injected build identity ${value}`);
  }
}

if (options['container-tag'] && options['container-tag'] !== `ghcr.io/devkrity/kautuka-base:${options.version}`) {
  fail(`container image tag ${options['container-tag']} does not match resolved version ${options.version}`);
}

if (options['platform-archive']) {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'kautuka-platform-verify-'));
  try {
    execFileSync('tar', ['-xzf', options['platform-archive'], '-C', tempRoot]);
    const files = await fs.readdir(tempRoot, { recursive: true });
    const frontendFiles = files.filter((entry) => entry.replaceAll('\\', '/').includes('services/webhost/wwwroot/assets/') && entry.endsWith('.js'));
    if (!frontendFiles.length) fail('platform archive has no frontend JavaScript bundles');
    const frontend = Buffer.concat(await Promise.all(frontendFiles.map((entry) => fs.readFile(path.join(tempRoot, entry))))).toString('utf8');
    const shortSha = options['source-sha'].slice(0, 7);
    for (const identity of [options.version, shortSha]) if (!frontend.includes(identity)) fail(`frontend bundle is missing injected identity ${identity}`);

    const binaryPaths = [
      'bin/kautuka-manager', 'services/gateway/kautuka-gateway',
      'services/credentialshost/kautuka-credentialshost', 'services/channelhost/kautuka-channelhost',
      'services/updateservice/kautuka-updateservice', 'services/webhost/kautuka-webhost',
    ];
    const informationalVersion = Buffer.from(`${options.version}+${shortSha}`);
    for (const relativePath of binaryPaths) {
      const binary = await fs.readFile(path.join(tempRoot, relativePath));
      if (!binary.includes(informationalVersion)) fail(`${relativePath} does not contain InformationalVersion ${informationalVersion.toString()}`);
    }
  } finally {
    await fs.rm(tempRoot, { recursive: true, force: true });
  }
}

const manifestPath = path.join(options.directory, 'kautuka-platform-release.json');
const signaturePath = path.join(options.directory, 'kautuka-platform-release.sig');
const manifestBytes = await fs.readFile(manifestPath);
const manifest = JSON.parse(manifestBytes.toString('utf8'));
if (manifest.version !== options.version) fail(`manifest version ${manifest.version} does not match ${options.version}`);
if (manifest.channel !== options.channel) fail(`manifest channel ${manifest.channel} does not match ${options.channel}`);
if (manifest.sourceSha !== options['source-sha']) fail(`manifest source SHA ${manifest.sourceSha} does not match ${options['source-sha']}`);
const manifestArtifactNames = (manifest.artifacts ?? []).map((artifact) => path.basename(artifact.path)).sort();
const requiredArtifactNames = ['kautuka-linux-amd64.tar.gz', 'kautuka-wsl-appliance.tar.gz'].sort();
if (JSON.stringify(manifestArtifactNames) !== JSON.stringify(requiredArtifactNames)) {
  fail(`manifest artifact file names [${manifestArtifactNames.join(', ')}] do not match required release files [${requiredArtifactNames.join(', ')}]`);
}

const publicKey = createPublicKey(await fs.readFile(options['public-key']));
const signature = Buffer.from((await fs.readFile(signaturePath, 'utf8')).trim(), 'base64');
if (!cryptoVerify('sha256', manifestBytes, publicKey, signature)) fail('manifest signature verification failed');

for (const artifact of manifest.artifacts ?? []) {
  const artifactPath = path.join(options.directory, artifact.path);
  const digest = createHash('sha256').update(await fs.readFile(artifactPath)).digest('hex');
  if (digest !== artifact.sha256) fail(`artifact checksum mismatch: ${artifact.path}`);
}

console.log(`release artifact checks passed: ${options.version} (${options.channel}) ${options['source-sha'].slice(0, 7)}; signature valid; ${manifest.artifacts?.length ?? 0} artifact checksums valid${options['dotnet-binary'] ? '; .NET InformationalVersion valid' : ''}${options['frontend-bundle'] || options['platform-archive'] ? '; frontend version/SHA valid' : ''}${options['platform-archive'] ? '; all Linux service InformationalVersions valid' : ''}${options['container-tag'] ? '; container tag valid' : ''}`);
