import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const runtimeImagesPath = path.join(repoRoot, 'src/backend/Kautuka.Manager/runtime-images.json');
const runtimeImages = JSON.parse(await readFile(runtimeImagesPath, 'utf8'));

for (const image of runtimeImages.images) {
  if (typeof image.archive !== 'string' || image.archive.length === 0) continue;
  if (!/^[a-z0-9-]+$/.test(image.id) || !/^[a-z0-9.-]+\.tar$/.test(image.archive)) {
    throw new Error(`Invalid runtime image archive entry for ${image.id}`);
  }
  process.stdout.write(`${image.id}\t${image.archive}\n`);
}
