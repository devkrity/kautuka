#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { readdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const args = Object.fromEntries(process.argv.slice(2).reduce((all, value, index, values) => {
  if (value.startsWith('--')) all.push([value.slice(2), values[index + 1]]);
  return all;
}, []));
const directory = path.resolve(args.directory ?? '');
const tag = args.tag;
if (!args.directory || !tag) {
  console.error('Usage: verify-release-assets.mjs --directory <flat asset directory> --tag <release tag>');
  process.exit(2);
}

const manifestPath = path.join(directory, 'kautuka-release-assets.json');
const checksumPath = path.join(directory, 'kautuka-release-assets.sha256');
const publicAssetNames = JSON.parse(await readFile(path.join(path.dirname(fileURLToPath(import.meta.url)), 'release-public-assets.json'), 'utf8'));
const maxGitHubReleaseAssetBytes = 2 * 1024 * 1024 * 1024;
const directoryEntries = await readdir(directory, { withFileTypes: true });
for (const entry of directoryEntries) {
  if (entry.isFile() && (await stat(path.join(directory, entry.name))).size >= maxGitHubReleaseAssetBytes) {
    throw new Error(`release asset is at or above GitHub's 2 GiB per-file limit: ${entry.name}`);
  }
}
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
if (manifest.schemaVersion !== 1 || !Array.isArray(manifest.assets) || manifest.assets.length === 0) {
  throw new Error('release asset manifest is invalid or empty');
}

const records = new Map();
for (const asset of manifest.assets) {
  if (!asset?.name || path.basename(asset.name) !== asset.name || records.has(asset.name)) {
    throw new Error(`release manifest contains a duplicate or non-flat asset name: ${asset?.name}`);
  }
  const expectedPrefix = `releases/${tag}/`;
  if (asset.relativePath !== `${expectedPrefix}${asset.name}`) {
    throw new Error(`release asset path does not match versioned tag ${tag}: ${asset.relativePath}`);
  }
  if (!Number.isSafeInteger(asset.sizeBytes) || asset.sizeBytes < 0 || asset.sizeBytes >= maxGitHubReleaseAssetBytes) {
    throw new Error(`release asset is at or above GitHub's 2 GiB per-file limit: ${asset.name}`);
  }
  const assetPath = path.join(directory, asset.name);
  const sizeBytes = (await stat(assetPath)).size;
  if (sizeBytes >= maxGitHubReleaseAssetBytes) {
    throw new Error(`release asset is at or above GitHub's 2 GiB per-file limit: ${asset.name}`);
  }
  const bytes = await readFile(assetPath);
  const sha256 = createHash('sha256').update(bytes).digest('hex');
  if (sizeBytes !== asset.sizeBytes || sha256 !== asset.sha256) {
    throw new Error(`release asset failed digest or size validation: ${asset.name}`);
  }
  records.set(asset.name, asset);
}

for (const name of publicAssetNames) {
  if (!records.has(name)) throw new Error(`required release asset is absent: ${name}`);
}

const unexpectedEntries = directoryEntries.filter((entry) => !entry.isFile());
if (unexpectedEntries.length > 0) {
  throw new Error(`release asset directory must be flat; found non-file entry: ${unexpectedEntries[0].name}`);
}
const directoryFiles = directoryEntries
  .map((entry) => entry.name)
  .sort();
const expectedNames = [...publicAssetNames, 'kautuka-release-assets.json', 'kautuka-release-assets.sha256'].sort();
if (JSON.stringify(directoryFiles) !== JSON.stringify(expectedNames)) {
  throw new Error(`release directory does not match its flat asset manifest; found [${directoryFiles.join(', ')}]`);
}

const checksumText = await readFile(checksumPath, 'utf8');
for (const asset of manifest.assets) {
  const expectedLine = `${asset.sha256}  ${asset.name}`;
  if (!checksumText.split(/\r?\n/u).includes(expectedLine)) {
    throw new Error(`checksums file is missing the verified digest for ${asset.name}`);
  }
}
console.log(`verified ${manifest.assets.length} flat assets for ${tag}`);
