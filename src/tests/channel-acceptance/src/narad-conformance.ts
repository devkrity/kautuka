import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import type { NaradRecord } from './narad-types.js';

const repoRoot = resolve(process.cwd(), '../../..');
const require = createRequire(resolve(repoRoot, 'package.json'));
const conformanceRoot = resolve(dirname(require.resolve('@devkrity/narad-spec/package.json')), 'conformance');

export async function loadConformanceTrace(name: string): Promise<readonly NaradRecord[]> {
  const text = await readFile(resolve(conformanceRoot, name), 'utf8');
  return text.split(/\r?\n/).filter((line) => line.trim()).map((line) => JSON.parse(line) as NaradRecord);
}

export const requiredConformanceTraces = [
  'snapshot-replay.jsonl',
  'interrupt-pause.jsonl',
  'run-graph.jsonl',
  'reconnect-revision.jsonl',
  'progress-replay.jsonl',
  'workspace-watch.jsonl',
  'client-tool-stop.jsonl',
  'authority-replay.jsonl',
  'independent-run.jsonl',
] as const;
