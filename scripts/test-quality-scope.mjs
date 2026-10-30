import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { classifyQualityScope } from './lib/quality-scope.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workflow = await readFile(path.join(root, '.github/workflows/dotnet-quality.yml'), 'utf8');

const cases = [
  { files: ['README.md', 'docs/releasing.md', 'LICENSE'], expected: 'docs' },
  { files: ['src/frontend/web/src/App.tsx', 'docs/releasing.md'], expected: 'frontend' },
  { files: ['src/frontend/web/package.json'], expected: 'frontend' },
  { files: ['src/frontend/web/public/assets/icons/kautuka.ico'], expected: 'full' },
  { files: ['README.md', 'src/backend/Kautuka.Gateway/Program.cs'], expected: 'full' },
  { files: ['src/frontend/web/src/App.tsx', 'mcps/kautuka-web-tools/main.py'], expected: 'full' },
  { files: ['mcps/kautuka-web-tools/pyproject.toml'], expected: 'full' },
  { files: ['.github/workflows/dotnet-quality.yml'], expected: 'full' },
  { files: ['pnpm-lock.yaml'], expected: 'full' },
  { files: ['src/backend/README.md'], expected: 'docs' },
  { files: [], expected: 'full' },
];

for (const { files, expected } of cases) {
  assert.equal(classifyQualityScope(files), expected, `Expected ${JSON.stringify(files)} to classify as ${expected}.`);
}

assert.match(workflow, /quality-change-scope:[\s\S]*?scope: \$\{\{ steps\.classify\.outputs\.scope \}\}/,
  'the classifier job must publish its validated scope to dependent jobs');
assert.match(workflow, /if: \$\{\{ needs\.quality-change-scope\.outputs\.scope == 'full' \}\}/,
  'unclassified and unknown changes must use the full validation suite');
assert.match(workflow, /case "\$SCOPE" in[\s\S]*?full\)[\s\S]*?frontend\)[\s\S]*?docs\)[\s\S]*?unknown quality scope[\s\S]*?failing closed/,
  'the required aggregator must validate each scope and fail closed on unknown scope values');

console.log(`Quality scope classifier and workflow wiring passed ${cases.length} cases.`);
