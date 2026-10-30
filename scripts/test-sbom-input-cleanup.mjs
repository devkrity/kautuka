import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..');
const tempRoot = mkdtempSync(path.join(os.tmpdir(), 'kautuka-sbom-cleanup-'));
const root = path.join(tempRoot, 'wsl-appliance');
const readonlyDir = path.join(root, 'usr', 'share', 'licenses');
const readonlyFile = path.join(readonlyDir, 'COPYING');

try {
  mkdirSync(readonlyDir, { recursive: true });
  writeFileSync(readonlyFile, 'fixture license\n');
  chmodSync(readonlyDir, 0o555);
  chmodSync(path.join(root, 'usr', 'share'), 0o555);
  chmodSync(path.join(root, 'usr'), 0o555);
  chmodSync(readonlyFile, 0o444);

  const bash = process.platform === 'win32' ? 'bash' : 'bash';
  const result = spawnSync(bash, [path.join(scriptDir, 'make-sbom-input-writable.sh'), root], {
    cwd: repoRoot,
    encoding: 'utf8',
  });
  assert.equal(result.status, 0, `${result.stderr}\n${result.stdout}`);
  assert.match(readFileSync(readonlyFile, 'utf8'), /fixture license/);

  rmSync(root, { recursive: true, force: true });
  assert.equal(existsSync(root), false, 'temporary SBOM tree should be removable after permission repair');
  console.log('PASS: read-only extracted SBOM trees can be scanned and cleaned up');
} finally {
  // Restore permissions if the test failed before exercising the helper.
  try { chmodSync(readonlyFile, 0o600); } catch {}
  try { chmodSync(readonlyDir, 0o700); } catch {}
  try { chmodSync(path.join(root, 'usr', 'share'), 0o700); } catch {}
  try { chmodSync(path.join(root, 'usr'), 0o700); } catch {}
  rmSync(tempRoot, { recursive: true, force: true });
}
