import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const workflow = await readFile(new URL('../.github/workflows/dotnet-quality.yml', import.meta.url), 'utf8');
const releaseWorkflow = await readFile(new URL('../.github/workflows/release-pipeline.yml', import.meta.url), 'utf8');
const buildPolicy = await readFile(new URL('../src/Directory.Build.props', import.meta.url), 'utf8');

assert.doesNotMatch(workflow, /quality-change-scope|classify-quality-scope|outputs\.scope/,
  'required quality validation must not depend on changed-file classification');
assert.doesNotMatch(workflow, /^\s+paths(?:-ignore)?:/m,
  'required quality workflows must not filter pushes or pull requests by changed paths');
assert.match(buildPolicy, /<EnforceCodeStyleInBuild>true<\/EnforceCodeStyleInBuild>/,
  'the full backend build must enforce compiler code-style diagnostics alongside whitespace formatting');
assert.match(buildPolicy, /<TreatWarningsAsErrors>true<\/TreatWarningsAsErrors>/,
  'compiler diagnostics must remain blocking when the formatter check focuses on whitespace');
assert.match(workflow, /dotnet format whitespace src\/Kautuka\.slnx/,
  'PR quality must check whitespace formatting across the solution');
assert.match(releaseWorkflow, /dotnet format whitespace src\/Kautuka\.slnx/,
  'release validation must check whitespace formatting across the solution');

const requiredJobs = [
  'quality-mcp',
  'quality-repository-hygiene',
  'quality-release-regressions',
  'quality-narad',
  'quality-frontend',
  'quality-dotnet',
  'quality-format',
  'packaging-smoke',
  'credentials-published-boundary',
  'gateway-native-aot',
  'acceptance-contract',
];

for (const job of requiredJobs) {
  const start = workflow.indexOf(`  ${job}:`);
  assert.notEqual(start, -1, `required full-repository job ${job} must exist`);
  const nextJob = workflow.indexOf('\n  ', start + 3);
  const block = workflow.slice(start, nextJob === -1 ? undefined : nextJob);
  assert.doesNotMatch(block, /^    if:/m, `${job} must run for every invocation of this workflow`);
}

for (const job of requiredJobs) {
  assert.match(workflow, new RegExp(`^      - ${job}$`, 'm'), `aggregator must require ${job}`);
}

console.log(`Full-repository quality workflow wiring passed (${requiredJobs.length} required jobs).`);
