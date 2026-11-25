import { readFile } from 'node:fs/promises';

const [manifestPath] = process.argv.slice(2);
const { runtimeImages, applianceDeployment } = JSON.parse(await readFile(manifestPath, 'utf8'));
const services = Object.entries(applianceDeployment.services);
if (!runtimeImages.length || !services.length) throw new Error('Release image inventory is empty');

const rows = new Map();
function addImage(id, image, source) {
  if (!/^[a-z0-9-]+$/.test(id) || !/^[a-z0-9][a-z0-9./_-]+@sha256:[a-f0-9]{64}$/.test(image) || rows.has(id)) {
    throw new Error(`Invalid or duplicate release image entry for ${id}`);
  }
  rows.set(id, source);
}

for (const { id, image } of runtimeImages) addImage(id, image, `registry:${image}`);
for (const [role, { image }] of services) {
  addImage(role, image, `oci-archive:/src/sbom-input/linux-platform/service-images/${role}.tar`);
}

for (const [id, source] of rows) {
  const name = id === 'kautuka-base' ? id : `kautuka-${id}`;
  process.stdout.write(`${name}-image.spdx.json\t${source}\n`);
}
