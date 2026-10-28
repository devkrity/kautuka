#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { mkdir, readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = Object.fromEntries(process.argv.slice(2).reduce((all, value, index, values) => {
  if (value.startsWith('--')) all.push([value.slice(2), values[index + 1]]);
  return all;
}, []));
if (!args.directory) {
  console.error('Usage: node scripts/generate-release-credits.mjs --directory <release-directory>');
  process.exit(2);
}

const directory = path.resolve(args.directory);
const SPDX_LICENSE_LIST_COMMIT = '31ba1a50e5397e00a304dbadc76531740e89ee48';
const lockfiles = [
  'pnpm-lock.yaml',
  'src/frontend/pnpm-lock.yaml',
  'tests/channel-acceptance/pnpm-lock.yaml',
  'mcps/kautuka-web-tools/uv.lock',
];
const lockfileHashes = {};
for (const file of lockfiles) {
  const bytes = await readFile(path.join(root, file));
  lockfileHashes[file] = createHash('sha256').update(bytes).digest('hex');
}
const files = (await readdir(directory)).filter((name) => name.endsWith('.spdx.json')).sort();
if (files.length < 5) {
  throw new Error(`Expected the release SPDX documents (Linux, WSL, sidecar images and base image); found ${files.length}.`);
}

const clean = (value) => String(value ?? '').replaceAll('|', '\\|').replace(/[\r\n]+/g, ' ').trim();
const packageRows = new Map();
const packageLicenses = new Map();
const extractedLicenseTexts = new Map();
for (const file of files) {
  const document = JSON.parse(await readFile(path.join(directory, file), 'utf8'));
  if (!Array.isArray(document.packages)) throw new Error(`${file} has no SPDX packages array.`);
  for (const item of document.hasExtractedLicensingInfos ?? []) {
    if (item.licenseId && item.extractedText) extractedLicenseTexts.set(item.licenseId, item.extractedText);
  }
  for (const pkg of document.packages) {
    if (!pkg.name || pkg.name === 'NOASSERTION') continue;
    const license = pkg.licenseDeclared && pkg.licenseDeclared !== 'NOASSERTION'
      ? pkg.licenseDeclared
      : pkg.licenseConcluded && pkg.licenseConcluded !== 'NOASSERTION'
        ? pkg.licenseConcluded
        : pkg.licenseInfoInFiles?.filter((item) => item !== 'NOASSERTION').join(' AND ') || 'NOT DETECTED';
    const purl = pkg.externalRefs?.find((item) => item.referenceType?.toLowerCase().includes('purl'))?.referenceLocator;
    const ecosystem = purl?.match(/^pkg:(npm|nuget|pypi)\//i)?.[1]?.toLowerCase();
    const source = pkg.downloadLocation && pkg.downloadLocation !== 'NOASSERTION' ? pkg.downloadLocation : purl ?? 'Not declared in SPDX';
    const record = {
      name: clean(pkg.name),
      version: clean(pkg.versionInfo || 'Not declared'),
      license: clean(license),
      source: clean(source),
      ecosystem,
      targets: new Set(),
    };
    const key = `${record.name}\0${record.version}\0${record.license}\0${record.source}`;
    const existing = packageRows.get(key) ?? record;
    existing.targets.add(file.replace('.spdx.json', ''));
    packageRows.set(key, existing);
    if (ecosystem) packageLicenses.set(key, { name: record.name, version: record.version, license, ecosystem });
  }
}
const frontendInventoryPath = path.join(directory, 'FRONTEND_DEPENDENCIES.json');
const frontendInventory = JSON.parse(await readFile(frontendInventoryPath, 'utf8'));
const packagesMissingPackageSpecificLicenseText = frontendInventory.packagesMissingPackageSpecificLicenseText ?? [];
const packagesMissingLicenseTextEvidence = frontendInventory.packagesMissingLicenseTextEvidence ?? [];
const packagesWithUnreviewedLicenseSupplement = frontendInventory.packagesWithUnreviewedLicenseSupplement ?? [];
if (frontendInventory.schemaVersion !== 1 || !Array.isArray(frontendInventory.packages) ||
    frontendInventory.packageCount !== frontendInventory.packages.length || frontendInventory.packages.length === 0) {
  throw new Error('The frontend production dependency inventory is missing, stale, or empty.');
}
if (packagesMissingLicenseTextEvidence.length > 0) {
  throw new Error(`Frontend packages have no package-specific or supplemental license text: ${packagesMissingLicenseTextEvidence.join(', ')}`);
}
if (packagesWithUnreviewedLicenseSupplement.length > 0) {
  throw new Error(`Frontend supplemental license evidence requires exact-version review before publication: ${packagesWithUnreviewedLicenseSupplement.join(', ')}`);
}
for (const item of frontendInventory.packages) {
  if (!item.name || !item.version || !item.licenseExpression || !item.source || !Array.isArray(item.licenseFiles)) {
    throw new Error('The frontend production dependency inventory contains an incomplete record.');
  }
  const record = {
    name: clean(item.name),
    version: clean(item.version),
    license: clean(item.licenseExpression),
    source: clean(item.source),
    ecosystem: 'npm',
    targets: new Set(['frontend production dependency graph']),
  };
  const key = `${record.name}\0${record.version}\0${record.license}\0${record.source}`;
  const existing = packageRows.get(key) ?? record;
  existing.targets.add('frontend production dependency graph');
  packageRows.set(key, existing);
  packageLicenses.set(key, { name: record.name, version: record.version, license: record.license, ecosystem: 'npm' });
  for (const file of item.licenseFiles) {
    if (!file.file || path.basename(file.file) !== file.file || !/^LICENSE-NPM-[a-f0-9]{64}\.txt$/.test(file.file) || !/^[a-f0-9]{64}$/.test(file.sha256)) {
      throw new Error(`Frontend license evidence is invalid for ${item.name}@${item.version}.`);
    }
    const bytes = await readFile(path.join(directory, 'licenses', file.file));
    const hash = createHash('sha256').update(bytes).digest('hex');
    if (hash !== file.sha256) throw new Error(`Frontend package license text hash mismatch for ${item.name}@${item.version}.`);
  }
  if (item.supplementalLicenseText) {
    const file = item.supplementalLicenseText;
    if (!file.file || path.basename(file.file) !== file.file || !/^LICENSE-NPM-[a-f0-9]{64}\.txt$/.test(file.file) ||
        !/^[a-f0-9]{64}$/.test(file.sha256) || !/^https:\/\//.test(file.sourceUrl ?? '') ||
        !/^[a-f0-9]{40}$/.test(file.sourceCommit ?? '') || !file.sourceUrl.includes(file.sourceCommit) || file.status !== 'reviewed') {
      throw new Error(`Frontend supplemental license evidence is incomplete or unreviewed for ${item.name}@${item.version}.`);
    }
    const bytes = await readFile(path.join(directory, 'licenses', file.file));
    const hash = createHash('sha256').update(bytes).digest('hex');
    if (hash !== file.sha256) throw new Error(`Frontend supplemental license text hash mismatch for ${item.name}@${item.version}.`);
  }
}
if (packageRows.size === 0) throw new Error('The release SPDX files did not identify any packages.');

const licenseTexts = new Map();
const loadLicenseText = async (id, isException = false) => {
  const key = `${isException ? 'exception:' : 'license:'}${id}`;
  if (licenseTexts.has(key)) return;
  const extracted = extractedLicenseTexts.get(id);
  if (extracted) {
    licenseTexts.set(key, { id, text: extracted, kind: isException ? 'exception' : 'license' });
    return;
  }
  if (!/^[A-Za-z0-9][A-Za-z0-9.-]*$/.test(id)) throw new Error(`Unsupported SPDX identifier in shipped dependency: ${id}`);
  const folder = isException ? 'exceptions' : 'text';
  const url = `https://raw.githubusercontent.com/spdx/license-list-data/${SPDX_LICENSE_LIST_COMMIT}/${folder}/${encodeURIComponent(id)}.txt`;
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Cannot retrieve SPDX ${isException ? 'exception' : 'license'} text ${id} from the pinned SPDX license list (${response.status}).`);
  licenseTexts.set(key, { id, text: await response.text(), kind: isException ? 'exception' : 'license' });
};

for (const item of packageLicenses.values()) {
  const expression = item.license;
  if (!expression || /\b(?:NOASSERTION|NOT DETECTED|NONE)\b/.test(expression)) {
    throw new Error(`Shipped ${item.ecosystem} dependency ${item.name}@${item.version} has no resolved SPDX license: ${expression || 'missing'}`);
  }
  const tokens = expression.match(/[A-Za-z0-9][A-Za-z0-9.+-]*/g) ?? [];
  const identifiers = tokens.filter((token) => !['AND', 'OR', 'WITH'].includes(token));
  if (!identifiers.length) throw new Error(`No SPDX license identifier found for ${item.name}@${item.version}: ${expression}`);
  for (const id of identifiers) {
    const tokenIndex = tokens.indexOf(id);
    await loadLicenseText(id, tokens[tokenIndex - 1] === 'WITH');
  }
}

const catalogPath = path.join(directory, 'THIRD_PARTY_COMPONENTS.json');
const catalog = JSON.parse(await readFile(catalogPath, 'utf8'));
const curatedRows = catalog.components.map((component) => ({
  name: clean(component.id),
  version: clean(component.version),
  license: clean(component.licenseExpression),
  source: clean(component.source),
  targets: new Set([component.image ? 'bundled image or release payload' : 'curated release component']),
}));
const merged = new Map(packageRows);
for (const row of curatedRows) {
  const key = `${row.name}\0${row.version}\0${row.license}\0${row.source}`;
  if (!merged.has(key)) merged.set(key, row);
}
const rows = [...merged.values()].sort((left, right) => left.name.localeCompare(right.name) || left.version.localeCompare(right.version));
const unknownCount = rows.filter((item) => item.license === 'NOT DETECTED' || item.license === 'NONE').length;
const targets = (set) => [...set].sort().join(', ');
const lines = [
  `# Kautuka ${catalog.releaseTag} credits and third-party inventory`,
  '',
  'This file combines SPDX documents generated from release payloads, the exact installed pnpm production dependency inventory for the frontend, and the reviewed component catalog. Frontend records are a conservative superset and can include packages used only during frontend build. It lists component name, version, license declaration, source locator, and release targets. Use the adjacent SPDX files, `FRONTEND_DEPENDENCIES.json`, and `THIRD_PARTY_NOTICES.txt` for machine-readable metadata and package-specific license texts.',
  '',
  `Inventory: ${rows.length} distinct package/component records across ${files.length} SPDX documents. ${unknownCount} record(s) have no detected license and require review before publication.`,
  '',
  'The frontend production package set and versions come from the installed pnpm graph produced from the frozen frontend lockfile. Other detected package versions come from release SPDX documents. The following lockfile hashes record repository dependency inputs; NuGet versions are represented by the release SPDX documents because no NuGet lockfiles are checked in.',
  '',
  '## Lockfile inputs',
  '',
  ...Object.entries(lockfileHashes).map(([file, hash]) => `- \`${file}\`: SHA-256 \`${hash}\``),
  '',
  '| Component | Version | License | Source | Included in |',
  '| --- | --- | --- | --- | --- |',
  ...rows.map((item) => `| ${item.name} | ${item.version} | ${item.license} | ${item.source} | ${targets(item.targets)} |`),
  '',
  '## Font and WebView2 notes',
  '',
  'The bundled IBM Plex Mono and Source Sans 3 font packages are listed above under OFL-1.1, with the complete license texts in `licenses/`. The Windows installer embeds `WebView2Loader.dll` via Photino.Native; the WebView2 Runtime itself is supplied by Windows and is not bundled with Kautuka.',
  '',
  '## Review before distribution',
  '',
  'Investigate every `NOT DETECTED` or `NONE` license entry and confirm its applicable license text and source availability before publishing the release. This generated inventory is evidence for review, not a legal conclusion.',
  ...(packagesMissingPackageSpecificLicenseText.length ? [
    '',
    `The frontend inventory also identified ${packagesMissingPackageSpecificLicenseText.length} installed package(s) without package-specific license or notice files. Their declared license expression and canonical SPDX text are recorded where available, but package-specific copyright/notice requirements still require review.`,
  ] : []),
  '',
];
await writeFile(path.join(directory, 'CREDITS.md'), lines.join('\n'));

const dependencyRows = [...packageLicenses.values()].sort((a, b) => a.ecosystem.localeCompare(b.ecosystem) || a.name.localeCompare(b.name) || a.version.localeCompare(b.version));
const noticeLines = [
  '',
  '## Shipped npm, NuGet, and Python dependencies',
  '',
  'Package versions below were identified in the release SPDX documents. License texts are included once per SPDX identifier below and in licenses/. Standard license texts come from the SPDX License List at a pinned commit; LicenseRef texts are copied from the release SBOM.',
  '',
  '| Ecosystem | Package | Version | SPDX license expression |',
  '| --- | --- | --- | --- |',
  ...dependencyRows.map((item) => `| ${item.ecosystem} | ${clean(item.name)} | ${clean(item.version)} | ${clean(item.license)} |`),
  ...(packagesMissingPackageSpecificLicenseText.length ? [
    '',
    '### Frontend packages without package-specific license or notice text in the installed npm package',
    '',
    'Canonical SPDX license texts are included where available, but those texts do not supply package-specific copyright or notice material. Review upstream package records for these packages before distribution:',
    '',
    ...packagesMissingPackageSpecificLicenseText.map((name) => `- ${clean(name)}`),
  ] : []),
  '',
  '## Dependency license texts',
  '',
  ...[...licenseTexts.values()].sort((a, b) => a.id.localeCompare(b.id) || a.kind.localeCompare(b.kind)).flatMap((item) => [
    `### ${item.kind === 'exception' ? 'SPDX exception' : 'SPDX license'}: ${item.id}`,
    '',
    '```text',
    item.text.trimEnd(),
    '```',
    '',
  ]),
];
const noticesPath = path.join(directory, 'THIRD_PARTY_NOTICES.txt');
const existingNotices = await readFile(noticesPath, 'utf8');
const generatedSection = '\n## Shipped npm, NuGet, and Python dependencies';
const generatedSectionIndex = existingNotices.indexOf(generatedSection);
const noticeBase = generatedSectionIndex < 0 ? existingNotices : existingNotices.slice(0, generatedSectionIndex);
await writeFile(noticesPath, `${noticeBase.trimEnd()}\n${noticeLines.join('\n').trimStart()}`);
const licenseDirectory = path.join(directory, 'licenses');
await mkdir(licenseDirectory, { recursive: true });
for (const item of licenseTexts.values()) {
  const filename = `LICENSE-${item.kind === 'exception' ? 'EXCEPTION-' : ''}${item.id}.txt`;
  await writeFile(path.join(licenseDirectory, filename), item.text);
}
console.log(`Generated CREDITS.md and dependency notices for ${dependencyRows.length} shipped npm/NuGet/Python records from ${files.length} SPDX documents; ${unknownCount} license(s) need review.`);
