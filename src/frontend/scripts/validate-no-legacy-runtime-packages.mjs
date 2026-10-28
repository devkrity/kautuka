#!/usr/bin/env node
/**
 * Fail when production frontend manifests, lockfiles, or source reference
 * legacy AG-UI, CopilotKit, or assistant-ui runtime packages.
 */
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const frontendRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(frontendRoot, '../..');

const forbiddenPackagePatterns = [
  /^@copilotkit\//,
  /^@assistant-ui\//,
  /^assistant-ui$/,
  /^@kautuka\/ag-ui-/,
  /^@ag-ui\//,
  /^@kautuka\/example-(copilotkit|assistant-ui|basic-ag-ui)/,
];

const forbiddenSourcePatterns = [
  /@copilotkit\//,
  /@assistant-ui\//,
  /@kautuka\/ag-ui-/,
  /@ag-ui\//,
  /from ['"]@copilotkit/,
  /from ['"]@assistant-ui/,
  /from ['"]@kautuka\/ag-ui/,
  /from ['"]@ag-ui/,
];

const manifestPaths = [
  path.join(frontendRoot, 'package.json'),
  path.join(frontendRoot, 'web/package.json'),
];

const allowedSourceFiles = new Set([
  path.normalize('web/src/test/productionDependencies.test.ts'),
  path.normalize('web/src/test/productionBundle.test.ts'),
  path.normalize('web/src/test/productionSourceExclusions.test.ts'),
  path.normalize('scripts/validate-no-legacy-runtime-packages.mjs'),
]);

const sourceRoots = [path.join(frontendRoot, 'web/src')];
const ignoredDirNames = new Set(['node_modules', 'dist', 'coverage', '.git']);
const sourceExtensions = new Set(['.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.json']);

const findings = [];

function rel(file) {
  return path.relative(frontendRoot, file).replaceAll('\\', '/');
}

function checkManifest(filePath, contents) {
  let parsed;
  try {
    parsed = JSON.parse(contents);
  } catch {
    findings.push({ file: rel(filePath), detail: 'invalid JSON manifest' });
    return;
  }

  for (const section of ['dependencies', 'devDependencies', 'peerDependencies', 'optionalDependencies']) {
    const record = parsed[section];
    if (!record || typeof record !== 'object') continue;
    for (const name of Object.keys(record)) {
      if (forbiddenPackagePatterns.some((pattern) => pattern.test(name))) {
        findings.push({ file: rel(filePath), detail: `forbidden ${section}: ${name}` });
      }
    }
  }

  const serialized = JSON.stringify(parsed);
  for (const pattern of forbiddenPackagePatterns) {
    if (pattern.test(serialized)) {
      findings.push({ file: rel(filePath), detail: `forbidden package reference in manifest body` });
      break;
    }
  }
}

function checkLockfile(filePath, contents) {
  const webImporterMatch = contents.match(/^  web:\n([\s\S]*?)(?=^  [^\s]|\Z)/m);
  const webImporter = webImporterMatch?.[1] ?? '';
  for (const pattern of forbiddenPackagePatterns) {
    if (webImporter.split('\n').some((line) => pattern.test(line.trim()))) {
      findings.push({ file: rel(filePath), detail: `forbidden package locked for web importer: ${pattern}` });
    }
  }
}

async function walkSource(currentPath) {
  const entries = await readdir(currentPath, { withFileTypes: true });
  for (const entry of entries) {
    const fullPath = path.join(currentPath, entry.name);
    if (entry.isDirectory()) {
      if (ignoredDirNames.has(entry.name)) continue;
      await walkSource(fullPath);
      continue;
    }

    const ext = path.extname(entry.name);
    if (!sourceExtensions.has(ext)) continue;

    const relativePath = rel(fullPath);
    if (allowedSourceFiles.has(relativePath)) continue;
    if (relativePath.endsWith('.test.ts') || relativePath.endsWith('.test.tsx')) continue;

    const contents = await readFile(fullPath, 'utf8');
    for (const pattern of forbiddenSourcePatterns) {
      if (pattern.test(contents)) {
        findings.push({ file: relativePath, detail: `forbidden runtime reference: ${pattern}` });
        break;
      }
    }
  }
}

for (const manifestPath of manifestPaths) {
  checkManifest(manifestPath, await readFile(manifestPath, 'utf8'));
}

checkLockfile(
  path.join(frontendRoot, 'pnpm-lock.yaml'),
  await readFile(path.join(frontendRoot, 'pnpm-lock.yaml'), 'utf8')
);

for (const root of sourceRoots) {
  await walkSource(root);
}

const rootLockPath = path.join(repoRoot, 'pnpm-lock.yaml');
try {
  const rootLock = await readFile(rootLockPath, 'utf8');
  for (const pattern of forbiddenPackagePatterns) {
    if (rootLock.includes('@kautuka/ag-ui-') || rootLock.includes('@ag-ui/client')) {
      findings.push({ file: path.relative(repoRoot, rootLockPath), detail: 'root lockfile still references legacy runtime packages' });
      break;
    }
  }
} catch {
  // root lock is optional for this workspace
}

if (findings.length > 0) {
  console.error('Legacy runtime package ratchet failed:');
  for (const finding of findings) {
    console.error(`- ${finding.file}: ${finding.detail}`);
  }
  process.exit(1);
}

console.log('Legacy runtime package ratchet passed.');
