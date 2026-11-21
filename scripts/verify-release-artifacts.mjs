#!/usr/bin/env node

import { createHash, createPublicKey, verify as cryptoVerify } from 'node:crypto';
import { createReadStream, promises as fs } from 'node:fs';
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
  const hash = createHash('sha256');
  let size = 0;
  for await (const chunk of createReadStream(artifactPath)) {
    hash.update(chunk);
    size += chunk.length;
  }
  if (hash.digest('hex') !== artifact.sha256) fail(`artifact checksum mismatch: ${artifact.path}`);
  if (artifact.size !== undefined && size !== artifact.size) fail(`artifact size mismatch: ${artifact.path}`);
}

const applianceRoles = ['credentialshost', 'channelhost', 'gateway', 'webhost', 'updateservice'];

async function verifyPlatformPayload(archive, deployment) {
  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'kautuka-platform-verify-'));
  try {
    execFileSync('tar', ['-xzf', archive, '-C', tempRoot]);

    if (deployment) {
      const services = deployment.services;
      if (!services || Object.keys(services).length !== applianceRoles.length) {
        throw new Error('signed appliance deployment must declare the five service images');
      }
      const imageDirectory = path.join(tempRoot, 'service-images');
      if (!(await fs.lstat(imageDirectory).catch(() => null))?.isDirectory()) {
        throw new Error('platform archive is missing a regular appliance service-images directory');
      }
      for (const role of applianceRoles) {
        const expected = /@(sha256:[a-f0-9]{64})$/.exec(services[role]?.image ?? '')?.[1];
        if (!expected) throw new Error(`signed image digest missing for appliance role ${role}`);
        const imagePath = path.join(imageDirectory, `${role}.tar`);
        if (!(await fs.lstat(imagePath).catch(() => null))?.isFile()) {
          throw new Error(`appliance image archive is missing or not a regular file: ${role}`);
        }
        const imageManifest = execFileSync('skopeo', ['inspect', '--raw', `oci-archive:${imagePath}`]);
        const actual = `sha256:${createHash('sha256').update(imageManifest).digest('hex')}`;
        if (actual !== expected) throw new Error(`appliance image digest mismatch for ${role}: expected ${expected}, got ${actual}`);
      }
    }

    if (options['platform-archive']) await verifyPlatformIdentity(tempRoot);
  } finally {
    await fs.rm(tempRoot, { recursive: true, force: true });
  }
}

async function verifyPlatformIdentity(tempRoot) {
  const files = await fs.readdir(tempRoot, { recursive: true });
  const frontendFiles = files.filter((entry) => entry.replaceAll('\\', '/').includes('services/webhost/wwwroot/assets/') && entry.endsWith('.js'));
  if (!frontendFiles.length) throw new Error('platform archive has no frontend JavaScript bundles');
  const frontend = Buffer.concat(await Promise.all(frontendFiles.map((entry) => fs.readFile(path.join(tempRoot, entry))))).toString('utf8');
  const shortSha = options['source-sha'].slice(0, 7);
  for (const identity of [options.version, shortSha]) {
    if (!frontend.includes(identity)) throw new Error(`frontend bundle is missing injected identity ${identity}`);
  }

  const binaryPaths = ['bin/kautuka-manager', ...applianceRoles.map((role) => `services/${role}/kautuka-${role}`)];
  const informationalVersion = Buffer.from(`${options.version}+${shortSha}`);
  for (const relativePath of binaryPaths) {
    const binary = await fs.readFile(path.join(tempRoot, relativePath));
    if (!binary.includes(informationalVersion)) {
      throw new Error(`${relativePath} does not contain InformationalVersion ${informationalVersion.toString()}`);
    }
  }
}

if (options['platform-archive'] || manifest.applianceDeployment) {
  const linuxArtifact = manifest.artifacts.find((artifact) => path.basename(artifact.path) === 'kautuka-linux-amd64.tar.gz');
  const archive = path.join(options.directory, linuxArtifact.path);
  try {
    if (options['platform-archive'] && await fs.realpath(options['platform-archive']) !== await fs.realpath(archive)) {
      throw new Error('--platform-archive must name the signed Linux artifact in the release directory');
    }
    await verifyPlatformPayload(archive, manifest.applianceDeployment);
  } catch (error) {
    fail(error.message);
  }
}

console.log(`release artifact checks passed: ${options.version} (${options.channel}) ${options['source-sha'].slice(0, 7)}; signature valid; ${manifest.artifacts?.length ?? 0} artifact checksums valid${options['dotnet-binary'] ? '; .NET InformationalVersion valid' : ''}${options['frontend-bundle'] || options['platform-archive'] ? '; frontend version/SHA valid' : ''}${options['platform-archive'] ? '; all Linux service InformationalVersions valid' : ''}${manifest.applianceDeployment ? '; all appliance OCI archive digests valid' : ''}${options['container-tag'] ? '; container tag valid' : ''}`);
