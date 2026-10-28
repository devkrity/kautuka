#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

const args = Object.fromEntries(process.argv.slice(2).reduce((all, value, index, values) => {
  if (value.startsWith('--')) all.push([value.slice(2), values[index + 1]]);
  return all;
}, []));
if (!args['web-root'] || !args['output-dir']) {
  console.error('Usage: generate-frontend-third-party-inventory.mjs --web-root <web-package-root> --output-dir <compliance-dir> [--package-list-file <test-fixture.json>]');
  process.exit(2);
}

const webRoot = path.resolve(args['web-root']);
const output = path.resolve(args['output-dir']);
const supplementRoot = path.resolve(args['supplemental-root'] ?? process.cwd());
const supplementCatalog = args['supplemental-catalog']
  ? JSON.parse(await readFile(path.resolve(args['supplemental-catalog']), 'utf8'))
  : { schemaVersion: 1, supplements: [] };
if (supplementCatalog.schemaVersion !== 1 || !Array.isArray(supplementCatalog.supplements)) {
  throw new Error('Frontend license supplement catalog is invalid.');
}
const supplements = new Map();
for (const supplement of supplementCatalog.supplements) {
  const key = `${supplement.name}@${supplement.version}`;
  if (supplements.has(key)) throw new Error(`Duplicate frontend license supplement record: ${key}`);
  if (!supplement.licenseExpression || !supplement.sourceUrl || !/^[a-f0-9]{40}$/.test(supplement.sourceCommit ?? '') ||
      !supplement.licenseFile || !/^[a-f0-9]{64}$/.test(supplement.sha256 ?? '') ||
      !['reviewed', 'review-required'].includes(supplement.status)) {
    throw new Error(`Frontend license supplement record is incomplete: ${key}`);
  }
  if (!supplement.sourceUrl.includes(supplement.sourceCommit)) throw new Error(`Frontend license supplement source is not pinned to its commit: ${key}`);
  const licensePath = path.resolve(supplementRoot, supplement.licenseFile);
  if (!licensePath.startsWith(`${supplementRoot}${path.sep}`)) throw new Error(`Frontend license supplement file escapes its evidence root: ${key}`);
  const bytes = await readFile(licensePath);
  const sha256 = createHash('sha256').update(bytes).digest('hex');
  if (sha256 !== supplement.sha256) throw new Error(`Frontend supplemental license text hash changed for ${key}.`);
  supplements.set(key, { ...supplement, _bytes: bytes });
}
const readPackageList = async () => {
  if (args['package-list-file']) return JSON.parse(await readFile(path.resolve(args['package-list-file']), 'utf8'));
  const result = process.platform === 'win32'
    ? execFileSync(process.env.ComSpec ?? 'cmd.exe', ['/d', '/s', '/c', 'pnpm.cmd list --prod --depth Infinity --json'], {
      cwd: webRoot, encoding: 'utf8', windowsHide: true,
    })
    : execFileSync('pnpm', ['list', '--prod', '--depth', 'Infinity', '--json'], {
      cwd: webRoot, encoding: 'utf8', windowsHide: true,
    });
  return JSON.parse(result);
};

const roots = await readPackageList();
if (!Array.isArray(roots) || roots.length === 0) throw new Error('pnpm did not return a production dependency tree.');
const packages = new Map();
const missingPaths = new Set();
const visitedContexts = new Set();
const visit = async (node) => {
  const contextKey = `${node.path ?? `${node.name ?? 'root'}@${node.version ?? ''}`}\0${Object.entries(node.dependencies ?? {}).map(([name, dependency]) => `${name}@${dependency.version}:${dependency.path ?? ''}`).sort().join('\n')}`;
  if (visitedContexts.has(contextKey)) return;
  visitedContexts.add(contextKey);
  const parentManifest = node.path ? JSON.parse(await readFile(path.join(node.path, 'package.json'), 'utf8')) : null;
  for (const [dependencyName, dependency] of Object.entries(node.dependencies ?? {})) {
    const key = `${dependencyName}@${dependency.version}`;
    const packageRoot = dependency.path;
    if (!packageRoot || !(await exists(path.join(packageRoot, 'package.json')))) {
      if (!(parentManifest?.optionalDependencies && Object.hasOwn(parentManifest.optionalDependencies, dependencyName))) {
        throw new Error(`Required installed frontend dependency is missing from pnpm's production tree: ${key}`);
      }
      // pnpm includes optional platform variants for other operating systems.
      // The unavailable package is excluded from this target's installed graph.
      missingPaths.add(key);
      continue;
    }
    const packageJson = JSON.parse(await readFile(path.join(packageRoot, 'package.json'), 'utf8'));
    if (packageJson.name !== dependencyName || packageJson.version !== dependency.version) {
      throw new Error(`Installed frontend package does not match pnpm's frozen production tree: ${key}`);
    }
    if (!packages.has(key)) {
      const licenseFiles = await findLicenseFiles(packageRoot);
      let licenseExpression = formatLicense(packageJson.license ?? packageJson.licenses);
      if (!licenseExpression) licenseExpression = inferSpdxFromText(licenseFiles[0]?.text ?? '');
      if (!licenseExpression) throw new Error(`No license expression or identifiable license text for frontend dependency ${key}.`);
      const supplement = supplements.get(key);
      if (supplement && supplement.licenseExpression !== licenseExpression) {
        throw new Error(`Supplemental license evidence disagrees with package metadata for ${key}.`);
      }
      packages.set(key, {
        name: packageJson.name,
        version: packageJson.version,
        licenseExpression,
        source: packageSource(packageJson, dependency.resolved),
        _licenseFiles: licenseFiles,
        _supplement: supplement,
      });
    }
    await visit(dependency);
  }
};
for (const root of roots) await visit(root);
if (packages.size === 0) throw new Error('The production frontend package graph was empty.');

const licensesDirectory = path.join(output, 'licenses');
await mkdir(licensesDirectory, { recursive: true });
const records = [];
for (const item of [...packages.values()].sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version))) {
  const licenseFiles = [];
  for (const file of item._licenseFiles) {
    const sha256 = createHash('sha256').update(file.bytes).digest('hex');
    const targetName = `LICENSE-NPM-${sha256}.txt`;
    const target = path.join(licensesDirectory, targetName);
    if (!(await exists(target))) await writeFile(target, file.bytes);
    licenseFiles.push({ file: targetName, sha256, originalName: file.name });
  }
  let supplementalLicenseText = null;
  if (item._supplement) {
    const sha256 = item._supplement.sha256;
    const targetName = `LICENSE-NPM-${sha256}.txt`;
    const target = path.join(licensesDirectory, targetName);
    if (!(await exists(target))) await writeFile(target, item._supplement._bytes);
    supplementalLicenseText = {
      file: targetName,
      sha256,
      sourceUrl: item._supplement.sourceUrl,
      sourceCommit: item._supplement.sourceCommit,
      sourcePath: item._supplement.sourcePath ?? null,
      status: item._supplement.status,
      evidence: item._supplement.evidence ?? null,
      publishedGitHead: item._supplement.publishedGitHead ?? null,
    };
  }
  records.push({
    name: item.name,
    version: item.version,
    licenseExpression: item.licenseExpression,
    source: item.source,
    licenseFiles,
    packageSpecificLicenseTextPresent: licenseFiles.length > 0,
    supplementalLicenseText,
  });
}

const inventory = {
  schemaVersion: 1,
  scope: 'Installed pnpm production dependency graph for src/frontend/web. This is a conservative superset: dependencies used only during frontend build may also appear.',
  packageCount: records.length,
  excludedUnavailablePackages: [...missingPaths].sort(),
  packagesMissingPackageSpecificLicenseText: records.filter((item) => !item.packageSpecificLicenseTextPresent).map((item) => `${item.name}@${item.version}`),
  packagesMissingLicenseTextEvidence: records.filter((item) => !item.packageSpecificLicenseTextPresent && !item.supplementalLicenseText).map((item) => `${item.name}@${item.version}`),
  packagesWithUnreviewedLicenseSupplement: records.filter((item) => item.supplementalLicenseText?.status === 'review-required').map((item) => `${item.name}@${item.version}`),
  packages: records,
};
await writeFile(path.join(output, 'FRONTEND_DEPENDENCIES.json'), `${JSON.stringify(inventory, null, 2)}\n`);
console.log(`Recorded ${records.length} installed production frontend packages and ${[...new Set(records.flatMap((item) => item.licenseFiles.map((file) => file.sha256)))].length} package license texts.`);

async function exists(file) {
  try { await readFile(file); return true; } catch { return false; }
}

async function findLicenseFiles(packageRoot) {
  const names = (await readdir(packageRoot)).filter((name) => /^(?:licen[cs]e|copying|notice)(?:[._-].*)?$/i.test(name)).sort();
  const files = [];
  for (const name of names) {
    const bytes = await readFile(path.join(packageRoot, name));
    // Reject binary/unreadable notices rather than silently omitting package terms.
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    files.push({ name, bytes, text });
  }
  return files;
}

function formatLicense(value) {
  if (typeof value === 'string') return value.trim();
  if (Array.isArray(value)) return value.map((item) => formatLicense(item)).filter(Boolean).join(' OR ');
  if (value && typeof value === 'object' && typeof value.type === 'string') return value.type.trim();
  return '';
}

function inferSpdxFromText(text) {
  const prefix = text.slice(0, 1200);
  const matches = [
    [/\bMIT License\b|\bThe MIT License\b/i, 'MIT'],
    [/\bApache License(?:,? Version)?\s*2(?:\.0)?\b/i, 'Apache-2.0'],
    [/\bISC License\b/i, 'ISC'],
    [/\bBSD 3-Clause\b/i, 'BSD-3-Clause'],
    [/\bBSD 2-Clause\b/i, 'BSD-2-Clause'],
    [/\bMozilla Public License(?: Version)?\s*2(?:\.0)?\b/i, 'MPL-2.0'],
    [/\bEclipse Public License(?: - v)?\s*2(?:\.0)?\b/i, 'EPL-2.0'],
  ];
  return matches.find(([pattern]) => pattern.test(prefix))?.[1] ?? '';
}

function packageSource(packageJson, resolved) {
  const repository = packageJson.repository;
  const url = typeof repository === 'string' ? repository : repository?.url;
  if (typeof url === 'string' && url.trim()) {
    const value = url.trim();
    if (/^github:[^/]+\/[^/]+$/.test(value)) return `https://github.com/${value.slice('github:'.length)}`;
    if (/^[^/:]+\/[^/:]+$/.test(value)) return `https://github.com/${value}`;
    if (/^git@[^:]+:.+$/.test(value)) {
      const [, host, repositoryPath] = value.match(/^git@([^:]+):(.+)$/);
      return `https://${host}/${repositoryPath.replace(/\.git$/, '')}`;
    }
    if (/^git\+ssh:\/\//.test(value)) return value.replace(/^git\+ssh:\/\/(?:git@)?/, 'https://').replace(/\.git$/, '');
    if (/^git:\/\//.test(value)) return value.replace(/^git:\/\//, 'https://').replace(/\.git$/, '');
    if (/^git\+https?:\/\//.test(value)) return value.replace(/^git\+/, '').replace(/\.git$/, '');
    if (/^https?:\/\//.test(value)) return value.replace(/\.git$/, '');
  }
  if (typeof resolved === 'string' && resolved.trim()) return resolved;
  return `https://www.npmjs.com/package/${encodeURIComponent(packageJson.name)}/v/${encodeURIComponent(packageJson.version)}`;
}
