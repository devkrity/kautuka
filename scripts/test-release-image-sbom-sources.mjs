import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const readRepoJson = async (name) => JSON.parse(await readFile(path.join(repoRoot, name), 'utf8'));
const { images: runtimeImages } = await readRepoJson('src/backend/Kautuka.Manager/runtime-images.json');
const boundary = await readRepoJson('src/images/kautuka-egress-boundary/release.json');
assert.deepEqual(runtimeImages.filter(({ id }) => id === 'egress-boundary').map(({ image }) => image), [boundary.image]);

const roles = ['channelhost', 'credentialshost', 'gateway', 'updateservice', 'webhost'];
const services = Object.fromEntries(roles.map((role) => [role, { image: `ghcr.io/devkrity/kautuka/appliance-${role}@sha256:${'a'.repeat(64)}` }]));
const manifest = { runtimeImages, applianceDeployment: { services } };
const work = await mkdtemp(path.join(os.tmpdir(), 'kautuka-release-sbom-'));

try {
  const manifestPath = path.join(work, 'manifest.json');
  const script = path.join(repoRoot, 'scripts/list-release-image-sbom-sources.mjs');
  const run = async (value) => {
    await writeFile(manifestPath, JSON.stringify(value));
    return spawnSync(process.execPath, [script, manifestPath], { encoding: 'utf8' });
  };
  const result = await run(manifest);
  assert.equal(result.status, 0, result.stderr);
  const rows = new Map(result.stdout.trimEnd().split(/\r?\n/).map((line) => line.split('\t')));
  assert.equal(rows.size, runtimeImages.length + roles.length);
  for (const { id, image } of runtimeImages) {
    const name = id === 'kautuka-base' ? id : `kautuka-${id}`;
    assert.equal(rows.get(`${name}-image.spdx.json`), `registry:${image}`);
  }
  for (const role of roles) {
    assert.equal(rows.get(`kautuka-${role}-image.spdx.json`), `oci-archive:/src/sbom-input/linux-platform/service-images/${role}.tar`);
  }

  const invalidReference = await run({ ...manifest, runtimeImages: [{ id: 'openobserve', image: 'example.invalid/image:latest' }] });
  assert.notEqual(invalidReference.status, 0, 'reject mutable registry references');
  const emptyDeployment = await run({ ...manifest, applianceDeployment: { services: {} } });
  assert.notEqual(emptyDeployment.status, 0, 'reject a missing appliance image inventory');
  console.log('Release SBOM inventory uses pinned registry images and the bundled OCI service archives.');
} finally {
  await rm(work, { recursive: true, force: true });
}
