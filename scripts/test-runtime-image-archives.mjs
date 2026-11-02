import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const runtimeImagesPath = path.join(repoRoot, 'src/backend/Kautuka.Manager/runtime-images.json');
const runtimeImages = JSON.parse(await readFile(runtimeImagesPath, 'utf8'));
const expected = runtimeImages.images
  .filter((image) => typeof image.archive === 'string' && image.archive.length > 0)
  .map(({ id, archive }) => `${id}\t${archive}`)
  .sort();
const result = spawnSync(process.execPath, [path.join(repoRoot, 'scripts/list-runtime-image-archives.mjs')], {
  cwd: repoRoot,
  encoding: 'utf8',
});

assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
const actual = result.stdout.trimEnd().split(/\r?\n/).filter(Boolean).sort();
assert.deepEqual(actual, expected, 'only named image archives should be scanned as docker archives');
assert.ok(!actual.some((entry) => entry.startsWith('kautuka-base\t')),
  'registry-pulled kautuka-base has no appliance archive and must be scanned separately');
console.log(`PASS: selected ${actual.length} named runtime image archives; null archive entries are skipped`);
