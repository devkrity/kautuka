import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const runtimeImagesPath = process.argv[2] ? path.resolve(process.argv[2]) : path.join(repoRoot, 'src/backend/Kautuka.Manager/runtime-images.json');
const runtimeImages = JSON.parse(await readFile(runtimeImagesPath, 'utf8'));

const images = runtimeImages.images ?? runtimeImages.runtimeImages;
if (!Array.isArray(images)) throw new Error('Expected a runtime inventory or signed release manifest');
for (const image of images) {
  if (typeof image.archive !== 'string' || image.archive.length === 0) continue;
  if (!/^[a-z0-9-]+$/.test(image.id) || !/^[a-z0-9.-]+\.(?:tar|oci)$/.test(image.archive)) {
    throw new Error(`Invalid runtime image archive entry for ${image.id}`);
  }
  process.stdout.write(`${image.id}\t${image.archive}\n`);
}
