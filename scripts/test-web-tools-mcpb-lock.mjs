import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const packageDir = path.join(repo, 'mcps/kautuka-web-tools');
const outputDir = await mkdtemp(path.join(tmpdir(), 'kautuka-web-tools-mcpb-'));

function hasMcpbOnPath() {
  const result = spawnSync(process.platform === 'win32' ? 'where.exe' : 'which', ['mcpb'], { stdio: 'ignore' });
  return result.status === 0;
}

function pack(source, output) {
  const args = ['pack', source, output];
  if (hasMcpbOnPath()) {
    execFileSync('mcpb', args, { stdio: 'inherit' });
  } else if (process.platform === 'win32') {
    const npmCli = path.join(path.dirname(process.execPath), 'node_modules/npm/bin/npx-cli.js');
    execFileSync(process.execPath, [npmCli, '--yes', '@anthropic-ai/mcpb@2.1.2', ...args], { stdio: 'inherit' });
  } else {
    execFileSync('npx', ['--yes', '@anthropic-ai/mcpb@2.1.2', ...args], { stdio: 'inherit' });
  }
}

function zipEntries(buffer) {
  const endSignature = Buffer.from([0x50, 0x4b, 0x05, 0x06]);
  const end = buffer.lastIndexOf(endSignature);
  assert.notEqual(end, -1, 'MCPB output must be a ZIP archive');
  const entryCount = buffer.readUInt16LE(end + 10);
  let offset = buffer.readUInt32LE(end + 16);
  const names = [];
  for (let index = 0; index < entryCount; index++) {
    assert.equal(buffer.readUInt32LE(offset), 0x02014b50, 'ZIP central directory entry must be valid');
    const nameLength = buffer.readUInt16LE(offset + 28);
    const extraLength = buffer.readUInt16LE(offset + 30);
    const commentLength = buffer.readUInt16LE(offset + 32);
    names.push(buffer.toString('utf8', offset + 46, offset + 46 + nameLength));
    offset += 46 + nameLength + extraLength + commentLength;
  }
  return names;
}

try {
  const output = path.join(outputDir, 'web-tools.mcpb');
  pack(packageDir, output);
  const entries = zipEntries(await readFile(output));
  assert(entries.some(entry => entry === 'uv.lock' || entry.endsWith('/uv.lock')),
    'the packed MCPB must contain uv.lock so its locked install path has the checked-in dependency graph');
  assert(entries.includes('pyproject.toml'), 'the packed MCPB must also contain pyproject.toml');
} finally {
  await rm(outputDir, { recursive: true, force: true });
}
