import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { assertNoSecretShapedEvidence } from './evidence.js';

const prohibitedRawCapture = new Set(['.trace', '.network', '.har']);
const inspectableText = new Set(['.json', '.log', '.txt']);

/** Reject raw browser captures and inspect every retained text artifact. */
export async function assertSafeRetainedArtifacts(directory: string): Promise<void> {
  let entries;
  try { entries = await readdir(directory, { withFileTypes: true }); }
  catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return;
    throw error;
  }
  for (const entry of entries) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) await assertSafeRetainedArtifacts(path);
    else {
      const extension = entry.name.slice(entry.name.lastIndexOf('.')).toLowerCase();
      if (prohibitedRawCapture.has(extension)) throw new Error(`Raw browser capture must not be retained: ${path}`);
      if (inspectableText.has(extension)) assertNoSecretShapedEvidence(await readFile(path, 'utf8'));
    }
  }
}
