#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { readdir, readFile, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';

const args = Object.fromEntries(process.argv.slice(2).reduce((all, value, index, values) => {
  if (value.startsWith('--')) all.push([value.slice(2), values[index + 1]]);
  return all;
}, []));
const directory = path.resolve(args.directory ?? '');
const prefix = (args['relative-prefix'] ?? '').replaceAll('\\', '/').replace(/^\/+|\/+$/g, '');
if (!args.directory || !args['relative-prefix']) {
  console.error('Usage: node scripts/generate-release-asset-manifest.mjs --directory <dir> --relative-prefix <public/path>');
  process.exit(2);
}

async function filesIn(folder) {
  const entries = await readdir(folder, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const filePath = path.join(folder, entry.name);
    if (entry.isDirectory()) files.push(...await filesIn(filePath));
    else if (entry.isFile() && !['kautuka-release-assets.sha256', 'kautuka-release-assets.json'].includes(entry.name)) files.push(filePath);
  }
  return files;
}

const files = (await filesIn(directory)).sort((a, b) => a.localeCompare(b, 'en'));
const records = [];
const sums = [];
for (const file of files) {
  const bytes = await readFile(file);
  const name = path.basename(file);
  const relativePath = `${prefix}/${path.relative(directory, file).split(path.sep).join('/')}`;
  records.push({ name, relativePath, sizeBytes: (await stat(file)).size, sha256: createHash('sha256').update(bytes).digest('hex') });
  sums.push(`${createHash('sha256').update(bytes).digest('hex')}  ${name}`);
}
await writeFile(path.join(directory, 'kautuka-release-assets.sha256'), `${sums.join('\n')}\n`);
await writeFile(path.join(directory, 'kautuka-release-assets.json'), `${JSON.stringify({ schemaVersion: 1, assets: records }, null, 2)}\n`);
