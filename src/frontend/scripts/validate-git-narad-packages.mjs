#!/usr/bin/env node
/**
 * Fail when Kautuka vendors Narad or consumes anything other than the published npm versions.
 */
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const frontendRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(frontendRoot, '../..');
const findings = [];
const npmVersion = '0.1.0';

const forbiddenTrees = [
  path.join(frontendRoot, 'packages/narad-client'),
  path.join(frontendRoot, 'packages/narad-react'),
  path.join(repoRoot, 'vendor/narad'),
];

for (const tree of forbiddenTrees) {
  if (existsSync(tree)) {
    findings.push(`${path.relative(repoRoot, tree).replaceAll('\\', '/')} must not exist; consume @devkrity/narad from npm`);
  }
}

const webManifest = JSON.parse(await readFile(path.join(frontendRoot, 'web/package.json'), 'utf8'));
const rootManifest = JSON.parse(await readFile(path.join(repoRoot, 'package.json'), 'utf8'));

for (const name of ['@devkrity/narad', '@devkrity/narad-react']) {
  const value = webManifest.dependencies?.[name];
  if (value !== npmVersion) {
    findings.push(`web/package.json ${name} must be ${npmVersion} from npm`);
  }
}

if (rootManifest.devDependencies?.['@devkrity/narad-spec'] !== npmVersion) {
  findings.push(`package.json @devkrity/narad-spec must be ${npmVersion} from npm`);
}

function assertNpmResolutions(lockText, lockLabel, packageNames) {
  if (lockText.includes('github.com/devkrity/narad') || lockText.includes('codeload.github.com/devkrity/narad')) {
    findings.push(`${lockLabel} must not pin Narad from git`);
  }

  for (const name of packageNames) {
    if (!lockText.includes(`'${name}@${npmVersion}':`)) {
      findings.push(`${lockLabel} must resolve ${name}@${npmVersion}`);
    }
  }
}

const frontendLock = await readFile(path.join(frontendRoot, 'pnpm-lock.yaml'), 'utf8');
const rootLock = await readFile(path.join(repoRoot, 'pnpm-lock.yaml'), 'utf8');
assertNpmResolutions(frontendLock, 'src/frontend/pnpm-lock.yaml', ['@devkrity/narad', '@devkrity/narad-react']);
assertNpmResolutions(rootLock, 'pnpm-lock.yaml', ['@devkrity/narad-spec']);

if (findings.length > 0) {
  console.error('Narad consume ratchet failed:');
  for (const finding of findings) console.error(`- ${finding}`);
  process.exit(1);
}

console.log('Narad consume ratchet passed.');
