#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFile, mkdir, writeFile, copyFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateFrontendLicenseCatalog, validateFrontendSupplementLock } from './validate-frontend-license-catalog.mjs';
import { validateReleaseRuntimeImages } from './validate-release-runtime-images.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = Object.fromEntries(process.argv.slice(2).reduce((all, value, index, values) => {
  if (value.startsWith('--')) all.push([value.slice(2), values[index + 1]]);
  return all;
}, []));
if (!args['output-dir'] || !args['release-tag']) {
  console.error('Usage: node scripts/generate-third-party-compliance.mjs --output-dir <dir> --release-tag <tag> [--base-image <image@sha256:digest>]');
  process.exit(2);
}

const readJson = async (file) => JSON.parse(await readFile(path.join(root, file), 'utf8'));
const catalog = await readJson('compliance/third-party-components.json');
const runtime = await readJson('src/backend/Kautuka.Manager/runtime-images.json');
validateReleaseRuntimeImages(runtime.images, catalog.components, args['base-image']);

for (const component of catalog.components) {
  for (const [relative, expected] of Object.entries(component.artifacts ?? {})) {
    const bytes = await readFile(path.join(root, relative));
    const actual = createHash('sha256').update(bytes).digest('hex');
    if (actual !== expected) throw new Error(`${relative} SHA-256 changed (${actual}); review source/version/license and update the catalog.`);
  }
}

const output = path.resolve(args['output-dir']);
await mkdir(output, { recursive: true });
const inventory = {
  schemaVersion: 1,
  releaseTag: args['release-tag'],
  applicationLicense: 'Apache-2.0',
  baseImage: args['base-image'] ?? null,
  components: catalog.components,
};

const notices = [
  `Kautuka ${args['release-tag']} - Third-party software notices`,
  '',
  'This release includes or distributes the components below. The component licenses apply to those components and do not change the Apache-2.0 license for Kautuka itself.',
  'The complete license texts are in the accompanying licenses/ directory.',
  '',
  ...catalog.components.flatMap((component) => [
    `${component.id} ${component.version}`,
    `  License: ${component.licenseExpression}`,
    `  Source: ${component.source}`,
    ...(component.sourceCommit ? [`  Source commit: ${component.sourceCommit}`] : []),
    ...(component.image ? [`  Distributed image: ${component.image}`] : []),
    ...(component.artifacts ? [`  Bundled files: ${Object.keys(component.artifacts).join(', ')}`] : []),
    ...(component.embeddedFiles ? [`  Embedded files: ${component.embeddedFiles.join(', ')}`] : []),
    ...(component.notes ? [`  Note: ${component.notes}`] : []),
    '',
  ]),
  ...(args['base-image'] ? [
    'kautuka-base release image',
    '  Package licenses: see the accompanying kautuka-base-image.spdx.json, which is generated from this exact immutable image digest.',
    `  Image: ${args['base-image']}`,
    '',
  ] : []),
];
await writeFile(path.join(output, 'THIRD_PARTY_NOTICES.txt'), `${notices.join('\n').trimEnd()}\n`);
const licensesDir = path.join(output, 'licenses');
await mkdir(licensesDir, { recursive: true });
for (const name of [
  'LICENSE-MIT-sqlite-vec.txt',
  'LICENSE-APACHE-sqlite-vec.txt',
  'LICENSE-openobserve.txt',
  'LICENSE-proxy.txt',
  'LICENSE-OFL-1.1-IBM-Plex-Mono.txt',
  'LICENSE-OFL-1.1-Source-Sans-3.txt',
  'LICENSE-EPL-2.0-elkjs.md',
  'LICENSE-EPL-2.0-elkjs-0.9.3.md',
  'LICENSE-APACHE-Photino-Native.txt',
]) {
  await copyFile(path.join(root, 'compliance/licenses', name), path.join(licensesDir, name));
}
execFileSync(process.execPath, [
  path.join(root, 'scripts/generate-frontend-third-party-inventory.mjs'),
  '--web-root', path.join(root, 'src/frontend/web'),
  '--output-dir', output,
  '--supplemental-root', root,
  '--supplemental-catalog', path.join(root, 'compliance/frontend-license-supplements.json'),
], { stdio: 'inherit' });
const frontendDependencies = JSON.parse(await readFile(path.join(output, 'FRONTEND_DEPENDENCIES.json'), 'utf8'));
inventory.frontendDependencies = frontendDependencies;
const frontendLock = await readFile(path.join(root, 'src/frontend/pnpm-lock.yaml'), 'utf8');
validateFrontendLicenseCatalog({ lockText: frontendLock, catalog, inventory: frontendDependencies });
const licenseSupplements = await readJson('compliance/frontend-license-supplements.json');
validateFrontendSupplementLock({ lockText: frontendLock, catalog: licenseSupplements, inventory: frontendDependencies });
await writeFile(path.join(output, 'THIRD_PARTY_COMPONENTS.json'), `${JSON.stringify(inventory, null, 2)}\n`);
const noticeText = await readFile(path.join(output, 'THIRD_PARTY_NOTICES.txt'), 'utf8');
await writeFile(path.join(output, 'THIRD_PARTY_NOTICES.txt'), `${noticeText.trimEnd()}\n\nFrontend production dependency inventory\n  Packages: ${frontendDependencies.packageCount}\n  Complete per-package versions, license expressions, source locators, and staged package license/notice text hashes: FRONTEND_DEPENDENCIES.json\n  SPDX license texts are added by the release credits step from the pinned SPDX License List.\n`);
if (frontendDependencies.packagesMissingPackageSpecificLicenseText.length > 0) {
  await writeFile(path.join(output, 'THIRD_PARTY_NOTICES.txt'), `${await readFile(path.join(output, 'THIRD_PARTY_NOTICES.txt'), 'utf8')}  These packages omit a package-specific license/notice file in the installed npm tarball; exact upstream supplemental evidence, text hashes, and review state are recorded in FRONTEND_DEPENDENCIES.json.\n`);
}
if (frontendDependencies.packagesMissingLicenseTextEvidence.length > 0 || frontendDependencies.packagesWithUnreviewedLicenseSupplement.length > 0) {
  const missing = frontendDependencies.packagesMissingLicenseTextEvidence.join(', ') || 'none';
  const pending = frontendDependencies.packagesWithUnreviewedLicenseSupplement.join(', ') || 'none';
  await writeFile(path.join(output, 'THIRD_PARTY_NOTICES.txt'), `${await readFile(path.join(output, 'THIRD_PARTY_NOTICES.txt'), 'utf8')}  License evidence without a reviewed exact-version supplemental text: ${missing}. Supplemental version/source association still requiring review: ${pending}. Do not publish until these review items are resolved.\n`);
}
