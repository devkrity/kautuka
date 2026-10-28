#!/usr/bin/env node
/**
 * Smoke: import public Narad exports from the installed git packages.
 */
import { dirname, resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { readFileSync } from 'node:fs';

const frontendRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webRoot = resolve(frontendRoot, 'web');

function resolvePackageImport(name) {
  const manifestPath = resolve(webRoot, 'node_modules', ...name.split('/'), 'package.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const importPath = manifest.exports?.['.']?.import ?? manifest.main;
  if (typeof importPath !== 'string') {
    throw new Error(`${name} has no exports["."].import`);
  }
  return resolve(dirname(manifestPath), importPath);
}

const exportsToSmoke = [
  {
    name: '@devkrity/narad',
    assert: (mod) => {
      if (typeof mod.createNaradReducer !== 'function') {
        throw new Error('createNaradReducer missing');
      }
      if (typeof mod.createNaradClientStore !== 'function') {
        throw new Error('createNaradClientStore missing');
      }
      if (typeof mod.createClientExecutor !== 'function') {
        throw new Error('createClientExecutor missing');
      }
    },
  },
  {
    name: '@devkrity/narad-react',
    assert: (mod) => {
      if (typeof mod.NaradProvider !== 'function') {
        throw new Error('NaradProvider missing');
      }
      if (typeof mod.useNaradClientStore !== 'function') {
        throw new Error('useNaradClientStore missing');
      }
      if (typeof mod.useNaradSnapshot !== 'function') {
        throw new Error('useNaradSnapshot missing');
      }
      if (typeof mod.useNaradSelector !== 'function') {
        throw new Error('useNaradSelector missing');
      }
    },
  },
];

let failed = 0;

for (const entry of exportsToSmoke) {
  try {
    const resolved = resolvePackageImport(entry.name);
    const mod = await import(pathToFileURL(resolved).href);
    entry.assert(mod);
    console.log(`✓ ${entry.name} dist import OK`);
  } catch (error) {
    failed += 1;
    console.error(`✗ ${entry.name} dist import FAILED`);
    console.error(error);
  }
}

if (failed > 0) {
  process.exitCode = 1;
  console.error(`\n${failed} public export smoke test(s) failed.`);
} else {
  console.log(`\nAll ${exportsToSmoke.length} public dist exports smoke-tested successfully.`);
}
