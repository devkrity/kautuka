#!/usr/bin/env node
import assert from 'node:assert/strict';
import { chmod, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const helper = path.join(root, 'scripts/find-github-release-by-tag.sh');
const pipeline = await readFile(path.join(root, '.github/workflows/release-pipeline.yml'), 'utf8');
const publisher = await readFile(path.join(root, '.github/workflows/release-publish-assets.yml'), 'utf8');
const temporary = await mkdtemp(path.join(os.tmpdir(), 'kautuka-release-lookup-'));
const mockGh = path.join(temporary, 'gh');
const tag = 'v1.2.3-preview.4';

try {
  await writeFile(mockGh, `#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == api && "$2" == --paginate && "$3" == --slurp && "$4" == "repos/devkrity/kautuka/releases?per_page=100" ]] || exit 64
case "$GH_MOCK_STATE" in
  pages) printf '%s' "$GH_MOCK_PAGES" ;;
  api-error) echo 'gh: Bad Gateway (HTTP 502)' >&2; exit 1 ;;
  *) exit 65 ;;
esac
`);
  await chmod(mockGh, 0o755);

  const release = { id: 123, tag_name: tag, target_commitish: '0123456789abcdef0123456789abcdef01234567', draft: true, prerelease: true, assets: [] };
  const run = (state, pages) => spawnSync('bash', [helper, 'devkrity/kautuka', tag], {
    encoding: 'utf8',
    env: { ...process.env, GH_BIN: mockGh, GH_MOCK_STATE: state, GH_MOCK_PAGES: pages ?? '' },
  });

  const draft = run('pages', JSON.stringify([[release], [{ id: 124, tag_name: 'v1.2.2' }]]));
  assert.equal(draft.status, 0, draft.stderr || 'draft lookup must succeed');
  assert.deepEqual(JSON.parse(draft.stdout), release,
    'the paginated releases collection must find drafts by exact tag');

  const missing = run('pages', '[[]]');
  assert.equal(missing.status, 0, missing.stderr || 'missing release lookup must succeed');
  assert.equal(missing.stdout.trim(), 'null', 'a missing release must be distinct from an API error');

  const duplicate = run('pages', JSON.stringify([[release, { ...release, id: 124 }]]));
  assert.notEqual(duplicate.status, 0, 'duplicate tag entries must fail closed');
  assert.match(duplicate.stderr, /multiple releases/u);

  const apiError = run('api-error');
  assert.notEqual(apiError.status, 0, 'API failures must not be reported as missing releases');

  const invalidTag = spawnSync('bash', [helper, 'devkrity/kautuka', 'preview'], {
    encoding: 'utf8',
    env: { ...process.env, GH_BIN: mockGh, GH_MOCK_STATE: 'pages' },
  });
  assert.notEqual(invalidTag.status, 0, 'release lookup must reject a non-version tag');

  assert.match(pipeline, /find-github-release-by-tag\.sh/u);
  assert.match(publisher, /find-github-release-by-tag\.sh/u);
  assert.doesNotMatch(`${pipeline}\n${publisher}`, /releases\/tags\//u,
    'release workflows must not use the REST by-tag lookup, which omits drafts');

  console.log('GitHub release lookup tests passed: paginated draft lookup, missing tag, duplicate tag, API failure, and strict tag validation.');
} finally {
  await rm(temporary, { recursive: true, force: true });
}
