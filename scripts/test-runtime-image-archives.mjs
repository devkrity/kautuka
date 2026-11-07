import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
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
assert.deepEqual(actual, expected, 'every named offline image archive must be scanned');
assert.ok(actual.some((entry) => entry.startsWith('kautuka-base\t')),
  'cold startup must include the released sandbox archive');
console.log(`PASS: selected ${actual.length} named runtime image archives; null archive entries are skipped`);

const temporary = await mkdtemp(path.join(tmpdir(), 'kautuka-runtime-manifest-'));
try {
  const manifest = path.join(temporary, 'release.json');
  await writeFile(manifest, JSON.stringify({ runtimeImages: [
    { id: 'core-gateway', archive: 'core-gateway.oci' },
    { id: 'egress-dns', archive: 'egress-dns.tar' },
  ] }));
  const generated = spawnSync(process.execPath, [path.join(repoRoot, 'scripts/list-runtime-image-archives.mjs'), manifest], { encoding: 'utf8' });
  assert.equal(generated.status, 0, generated.stderr);
  assert.deepEqual(generated.stdout.trim().split(/\r?\n/), ['core-gateway\tcore-gateway.oci', 'egress-dns\tegress-dns.tar']);
  await writeFile(manifest, JSON.stringify({ runtimeImages: [{ id: 'core-gateway', archive: '../escape.oci' }] }));
  const invalid = spawnSync(process.execPath, [path.join(repoRoot, 'scripts/list-runtime-image-archives.mjs'), manifest], { encoding: 'utf8' });
  assert.notEqual(invalid.status, 0, 'manifest archive paths must remain portable basenames');
} finally {
  const resolved = await realpath(temporary);
  assert.ok(resolved.startsWith(path.join(await realpath(tmpdir()), 'kautuka-runtime-manifest-')));
  await rm(resolved, { recursive: true });
}
