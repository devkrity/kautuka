#!/usr/bin/env node
import assert from 'node:assert/strict';
import { chmod, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const resolver = path.join(root, 'scripts/resolve-preview-retry.sh');
const workflow = await readFile(path.join(root, '.github/workflows/release-pipeline.yml'), 'utf8');
const temporary = await mkdtemp(path.join(os.tmpdir(), 'kautuka-preview-retry-'));
const mockGh = path.join(temporary, 'gh');
const tag = 'v1.2.3-preview.4';
const sha = '0123456789abcdef0123456789abcdef01234567';

try {
  await writeFile(mockGh, `#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == api && "$2" == --paginate && "$3" == --slurp && "$4" == "repos/devkrity/kautuka/releases?per_page=100" ]] || exit 64
case "$GH_MOCK_STATE" in
  not-found) printf '[[]]' ;;
  api-error) echo 'gh: Bad Gateway (HTTP 502)' >&2; exit 1 ;;
  json) printf '[[%s]]' "$GH_MOCK_RELEASE" ;;
  *) exit 65 ;;
esac
`);
  await chmod(mockGh, 0o755);

  const run = (state, release = undefined, { tagSha = sha } = {}) => {
    try {
      return {
        status: 0,
        stdout: execFileSync('bash', [resolver, 'devkrity/kautuka', tag, tagSha, sha], {
          cwd: root,
          encoding: 'utf8',
          env: {
            ...process.env,
            GH_BIN: mockGh,
            GH_MOCK_STATE: state,
            GH_MOCK_RELEASE: release === undefined ? '' : JSON.stringify(release),
          },
        }).trim(),
      };
    } catch (error) {
      return { status: error.status ?? 1, stderr: String(error.stderr ?? '') };
    }
  };

  const draft = (overrides = {}) => ({
    tag_name: tag,
    target_commitish: sha,
    prerelease: true,
    draft: true,
    immutable: false,
    assets: [],
    ...overrides,
  });
  const published = (overrides = {}) => ({
    ...draft(),
    draft: false,
    immutable: true,
    assets: [{ name: 'kautuka-release-assets.json' }],
    ...overrides,
  });

  assert.equal(run('not-found').stdout, 'reuse', 'missing release reuses the exact existing tag');
  assert.equal(run('json', draft()).stdout, 'reuse', 'empty matching draft reuses the exact existing tag');
  assert.equal(run('json', published()).stdout, 'skip', 'only a matching immutable published release skips');

  for (const [name, result] of [
    ['mutable published release', run('json', published({ immutable: false }))],
    ['wrong source', run('json', published({ target_commitish: 'fedcba9876543210fedcba9876543210fedcba98' }))],
    ['wrong channel type', run('json', published({ prerelease: false }))],
    ['mismatched draft', run('json', draft({ target_commitish: 'fedcba9876543210fedcba9876543210fedcba98' }))],
    ['partial draft', run('json', draft({ assets: [{ name: 'partial.zip' }] }))],
    ['API failure', run('api-error')],
    ['changed main', run('not-found', undefined, { tagSha: 'fedcba9876543210fedcba9876543210fedcba98' })],
  ]) {
    assert.notEqual(result.status, 0, `${name} must fail closed`);
  }

  const retryBlock = workflow.match(/preview_retry=.*resolve-preview-retry\.sh[\s\S]*?git tag -a "\$release_tag"[\s\S]*?fi/);
  assert.ok(retryBlock, 'scheduled Preview resolution invokes the recovery helper before creating a new tag');
  assert.match(retryBlock[0], /reuse[\s\S]*release_tag="\$newest_tag"/);
  assert.match(retryBlock[0], /skip[\s\S]*skip=true/);
  assert.match(retryBlock[0], /else[\s\S]*next_number=\$\(\(newest_number \+ 1\)\)[\s\S]*git tag -a/);

  console.log('Preview retry resolution reuses only exact unpublished tags, skips only matching immutable releases, and fails closed on unsafe states');
} finally {
  await rm(temporary, { recursive: true, force: true });
}
