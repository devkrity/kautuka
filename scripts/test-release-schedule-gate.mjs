#!/usr/bin/env node
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workflow = await readFile(path.join(root, '.github/workflows/release-pipeline.yml'), 'utf8');
const jobsText = workflow.split(/^jobs:\s*$/m)[1];
if (!jobsText) throw new Error('Release pipeline has no jobs section.');

const jobs = new Map();
let currentName = null;
let currentLines = [];
for (const line of jobsText.split(/\r?\n/)) {
  const jobHeader = /^  ([a-z0-9-]+):$/.exec(line);
  if (jobHeader) {
    if (currentName) jobs.set(currentName, currentLines.join('\n'));
    currentName = jobHeader[1];
    currentLines = [];
  } else if (currentName) {
    currentLines.push(line);
  }
}
if (currentName) jobs.set(currentName, currentLines.join('\n'));

const resolve = jobs.get('resolve-channel');
if (!resolve) throw new Error('Release pipeline is missing resolve-channel.');
const gate = /^    if:\s*\$\{\{\s*(.*?)\s*\}\}\s*$/m.exec(resolve)?.[1];
if (gate !== "github.event_name == 'workflow_dispatch' || vars.KAUTUKA_RELEASES_ENABLED == 'true'") {
  throw new Error('Channel resolution must default off for scheduled runs and remain available to manual dispatch.');
}

if (!/^      publish_preview_now:\r?\n        description:.*\r?\n        required: true\r?\n        default: false\r?\n        type: boolean\s*$/m.test(workflow)) {
  throw new Error('Immediate Preview publication must require its explicit boolean opt-in, defaulting to false.');
}
if (!resolve.includes('PUBLISH_PREVIEW_NOW: ${{ inputs.publish_preview_now }}')
  || !resolve.includes('if [[ "$EVENT_NAME" == \'workflow_dispatch\' && "$PUBLISH_PREVIEW_NOW" == \'true\' ]]')
  || !resolve.includes('if [[ "$DRY_RUN" != \'false\' ]]')) {
  throw new Error('Immediate Preview publication must be manually opted into and require dry_run=false.');
}
if (!resolve.includes("git config user.name 'github-actions[bot]'")
  || !resolve.includes("git config user.email '41898282+github-actions[bot]@users.noreply.github.com'")) {
  throw new Error('Preview tags must be created with the GitHub Actions bot identity.');
}

const scheduleGateAllows = (event, enabled) => event === 'workflow_dispatch' || enabled === 'true';
for (const [event, enabled, expected] of [
  ['schedule', undefined, false],
  ['schedule', 'false', false],
  ['schedule', 'TRUE', false],
  ['schedule', 'true', true],
  ['workflow_dispatch', undefined, true],
  ['workflow_dispatch', 'false', true],
]) {
  if (scheduleGateAllows(event, enabled) !== expected) {
    throw new Error(`Unexpected schedule-gate result for event=${event}, variable=${enabled ?? '<unset>'}.`);
  }
}

function dependencies(name) {
  const block = jobs.get(name);
  if (!block) throw new Error(`Release pipeline job is missing: ${name}`);
  const lines = block.split('\n');
  const index = lines.findIndex((line) => /^    needs:/.test(line));
  if (index < 0) return [];
  const inline = /^    needs:\s*([a-z0-9-]+)\s*$/.exec(lines[index]);
  if (inline) return [inline[1]];
  const list = [];
  for (let cursor = index + 1; cursor < lines.length && /^      - [a-z0-9-]+\s*$/.test(lines[cursor]); cursor += 1) {
    list.push(lines[cursor].trim().slice(2));
  }
  if (list.length === 0) throw new Error(`Could not parse needs for ${name}.`);
  return list;
}

const cache = new Map();
function dependsOnResolver(name, visiting = new Set()) {
  if (name === 'resolve-channel') return true;
  if (cache.has(name)) return cache.get(name);
  if (visiting.has(name)) throw new Error(`Release workflow dependency cycle includes ${name}.`);
  visiting.add(name);
  const result = dependencies(name).some((dependency) => dependsOnResolver(dependency, visiting));
  visiting.delete(name);
  cache.set(name, result);
  return result;
}

for (const name of jobs.keys()) {
  if (name !== 'resolve-channel' && !dependsOnResolver(name)) {
    throw new Error(`Downstream release job ${name} can run without successful channel resolution.`);
  }
}

console.log('scheduled release gate defaults off; manual dispatch remains usable; all downstream jobs require the resolver');
