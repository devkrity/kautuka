#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateFrontendLicenseCatalog, validateFrontendSupplementLock } from './validate-frontend-license-catalog.mjs';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptDir, '..');
const temp = await mkdtemp(path.join(os.tmpdir(), 'kautuka-frontend-compliance-'));
try {
  const webRoot = path.join(temp, 'web');
  const elkjsPeerARoot = path.join(temp, 'packages', 'elkjs-peer-a');
  const elkjsPeerBRoot = path.join(temp, 'packages', 'elkjs-peer-b');
  const parentARoot = path.join(temp, 'packages', 'parent-a');
  const parentBRoot = path.join(temp, 'packages', 'parent-b');
  const childARoot = path.join(temp, 'packages', 'child-a');
  const childBRoot = path.join(temp, 'packages', 'child-b');
  const missingOptionalRoot = path.join(temp, 'packages', 'parent-with-optional');
  const output = path.join(temp, 'output');
  const staleOutput = path.join(temp, 'stale-output');
  await Promise.all([webRoot, elkjsPeerARoot, elkjsPeerBRoot, parentARoot, parentBRoot, childARoot, childBRoot, missingOptionalRoot].map((directory) => mkdir(directory, { recursive: true })));
  await writeFile(path.join(webRoot, 'package.json'), JSON.stringify({ name: 'fixture-web', dependencies: { elkjs: '0.12.0' } }));
  const elkjsLicense = 'Eclipse Public License - v 2.0\nCopyright (c) Kautuka fixture copyright\n';
  const elkjsNotice = 'elkjs fixture notice retained byte-for-byte\n';
  const childLicense = 'MIT License\nCopyright (c) Child fixture copyright\n';
  const noTextPackage = { name: 'child-a', version: '1.2.3', license: 'MIT', repository: { url: 'git+https://example.test/child.git' } };
  const makePackage = (directory, manifest, license) => Promise.all([
    writeFile(path.join(directory, 'package.json'), JSON.stringify(manifest)),
    ...(license ? [writeFile(path.join(directory, 'LICENSE'), license)] : []),
  ]);
  const elkjsManifest = { name: 'elkjs', version: '0.12.0', license: 'EPL-2.0 OR GPL-3.0-or-later', repository: 'https://github.com/kieler/elkjs.git', optionalDependencies: { 'unavailable-platform-binary': '1.0.0' } };
  await makePackage(elkjsPeerARoot, elkjsManifest, elkjsLicense);
  await writeFile(path.join(elkjsPeerARoot, 'NOTICE'), elkjsNotice);
  await makePackage(elkjsPeerBRoot, elkjsManifest, elkjsLicense);
  await makePackage(childARoot, noTextPackage, '');
  await makePackage(childBRoot, { ...noTextPackage, name: 'child-b' }, childLicense);
  await makePackage(missingOptionalRoot, { name: 'optional-parent', version: '1.0.0', license: 'MIT', optionalDependencies: { 'unavailable-platform-binary': '1.0.0' } }, childLicense);
  const listPath = path.join(temp, 'pnpm-list.json');
  const supplementLicensePath = path.join(temp, 'evidence', 'child-a-mit.txt');
  await mkdir(path.dirname(supplementLicensePath), { recursive: true });
  await writeFile(supplementLicensePath, 'MIT License\nCopyright (c) Child A upstream fixture\n');
  const supplementSha = createHash('sha256').update(await readFile(supplementLicensePath)).digest('hex');
  const testCommit = '1234567890123456789012345678901234567890';
  const supplementCatalogPath = path.join(temp, 'supplements.json');
  const supplementCatalog = { schemaVersion: 1, supplements: [
    { name: 'child-a', version: '1.2.3', licenseExpression: 'MIT', npmIntegrity: 'sha512-fixture', sourceUrl: `https://github.com/example/child-a/blob/${testCommit}/README.md`, sourceCommit: testCommit, sourcePath: 'README.md#License', licenseFile: 'evidence/child-a-mit.txt', sha256: supplementSha, status: 'reviewed' },
    { name: 'child-b', version: '1.2.3', licenseExpression: 'MIT', npmIntegrity: 'sha512-fixture', sourceUrl: `https://github.com/example/child-b/blob/${testCommit}/README.md`, sourceCommit: testCommit, sourcePath: 'README.md#License', licenseFile: 'evidence/child-a-mit.txt', sha256: supplementSha, status: 'review-required' },
  ] };
  await writeFile(supplementCatalogPath, `${JSON.stringify(supplementCatalog, null, 2)}\n`);
  const lockPath = path.join(temp, 'pnpm-lock.yaml');
  const supplementLock = 'packages:\n  child-a@1.2.3:\n    resolution: {integrity: sha512-fixture}\n  child-b@1.2.3:\n    resolution: {integrity: sha512-fixture}\n';
  await writeFile(lockPath, supplementLock);
  const unavailable = { version: '1.0.0', path: path.join(temp, 'missing-optional'), resolved: 'https://registry.npmjs.org/unavailable-platform-binary/-/unavailable-platform-binary-1.0.0.tgz' };
  await makePackage(parentARoot, { name: 'parent-a', version: '1.0.0', license: 'MIT' }, childLicense);
  await makePackage(parentBRoot, { name: 'parent-b', version: '1.0.0', license: 'MIT' }, childLicense);
  const list = [{ name: 'fixture-web', path: webRoot, dependencies: {
    'parent-a': { version: '1.0.0', path: parentARoot, dependencies: { elkjs: { version: '0.12.0', path: elkjsPeerARoot, dependencies: { 'child-a': { version: '1.2.3', path: childARoot }, 'unavailable-platform-binary': unavailable } } } },
    'parent-b': { version: '1.0.0', path: parentBRoot, dependencies: { elkjs: { version: '0.12.0', path: elkjsPeerBRoot, dependencies: { 'child-b': { version: '1.2.3', path: childBRoot }, 'unavailable-platform-binary': unavailable } } } },
    'optional-parent': { version: '1.0.0', path: missingOptionalRoot, dependencies: { 'unavailable-platform-binary': unavailable } },
  } }];
  await writeFile(listPath, `${JSON.stringify(list, null, 2)}\n`);

  const run = (outputDir) => spawnSync(process.execPath, [
    path.join(scriptDir, 'generate-frontend-third-party-inventory.mjs'),
    '--web-root', webRoot,
    '--output-dir', outputDir,
    '--package-list-file', listPath,
    '--supplemental-root', temp,
    '--supplemental-catalog', supplementCatalogPath,
  ], { cwd: root, encoding: 'utf8' });
  const success = run(output);
  if (success.status !== 0) throw new Error(`inventory generation failed: ${success.stderr}${success.stdout}`);
  const inventoryText = await readFile(path.join(output, 'FRONTEND_DEPENDENCIES.json'), 'utf8');
  const inventory = JSON.parse(inventoryText);
  if (inventory.packageCount !== 6 || inventory.packages.length !== 6) throw new Error('the generated inventory omitted a peer-context or production transitive package');
  const inventoriedElkjs = inventory.packages.find((item) => item.name === 'elkjs');
  if (!inventory.packages.some((item) => item.name === 'child-b') || inventoriedElkjs?.source !== 'https://github.com/kieler/elkjs') {
    throw new Error('the second peer-context transitive package or normalized upstream source locator was not recorded');
  }
  if (inventory.excludedUnavailablePackages.length !== 1 || !inventory.packagesMissingPackageSpecificLicenseText.includes('child-a@1.2.3')) {
    throw new Error('unavailable optional or missing package-specific license text was not reported accurately');
  }
  const supplemental = inventory.packages.find((item) => item.name === 'child-a').supplementalLicenseText;
  if (supplemental?.status !== 'reviewed' || inventory.packagesMissingLicenseTextEvidence.includes('child-a@1.2.3') ||
      !inventory.packagesWithUnreviewedLicenseSupplement.includes('child-b@1.2.3')) {
    throw new Error('supplemental license evidence status was not retained or failed to distinguish review state');
  }
  if (inventoryText.includes(temp)) throw new Error('the generated inventory contains a machine-specific package path');
  for (const pkg of inventory.packages) {
    if (pkg.packageSpecificLicenseTextPresent !== (pkg.licenseFiles.length > 0)) throw new Error(`package-specific license text status is inconsistent for ${pkg.name}`);
    for (const file of pkg.licenseFiles) {
      const bytes = await readFile(path.join(output, 'licenses', file.file));
      if (createHash('sha256').update(bytes).digest('hex') !== file.sha256) throw new Error(`bad license text hash for ${pkg.name}`);
    }
  }
  const allLicenseText = (await Promise.all(inventory.packages.flatMap((pkg) => [
    ...pkg.licenseFiles.map((file) => file.file),
    ...(pkg.supplementalLicenseText ? [pkg.supplementalLicenseText.file] : []),
  ].map((file) => readFile(path.join(output, 'licenses', file), 'utf8'))))).join('\n');
  if (!allLicenseText.includes('Kautuka fixture copyright') || !allLicenseText.includes('Child fixture copyright') || !allLicenseText.includes(elkjsNotice.trim())) {
    throw new Error('package-specific copyright or notice text was lost during staging');
  }

  const releaseCatalog = JSON.parse(await readFile(path.join(root, 'compliance/third-party-components.json'), 'utf8'));
  const releaseLock = await readFile(path.join(root, 'src/frontend/pnpm-lock.yaml'), 'utf8');
  const releaseInventory = { packages: [
    { name: 'elkjs', version: '0.12.0', licenseExpression: 'EPL-2.0 OR GPL-3.0-or-later' },
    { name: 'elkjs', version: '0.9.3', licenseExpression: 'EPL-2.0' },
  ] };
  validateFrontendLicenseCatalog({ lockText: releaseLock, catalog: releaseCatalog, inventory: releaseInventory });
  const actualSupplements = JSON.parse(await readFile(path.join(root, 'compliance/frontend-license-supplements.json'), 'utf8'));
  const actualSupplementOutput = path.join(temp, 'actual-supplement-output');
  const actualSupplementRun = spawnSync(process.execPath, [
    path.join(scriptDir, 'generate-frontend-third-party-inventory.mjs'),
    '--web-root', path.join(root, 'src/frontend/web'),
    '--output-dir', actualSupplementOutput,
    '--supplemental-root', root,
    '--supplemental-catalog', path.join(root, 'compliance/frontend-license-supplements.json'),
  ], { cwd: root, encoding: 'utf8' });
  if (actualSupplementRun.status !== 0) throw new Error(`real frontend license supplements failed: ${actualSupplementRun.stderr}${actualSupplementRun.stdout}`);
  const actualFrontendInventory = JSON.parse(await readFile(path.join(actualSupplementOutput, 'FRONTEND_DEPENDENCIES.json'), 'utf8'));
  validateFrontendSupplementLock({ lockText: releaseLock, catalog: actualSupplements, inventory: actualFrontendInventory });
  const reviewedReactSupplement = actualSupplements.supplements.find((item) => item.name === 'react-remove-scroll-bar' && item.version === '2.3.8');
  const reviewedReactInventory = actualFrontendInventory.packages.find((item) => item.name === 'react-remove-scroll-bar' && item.version === '2.3.8');
  if (reviewedReactSupplement?.status !== 'reviewed' || reviewedReactSupplement.sourceCommit !== '8ca9ba5ea52de03308fe8ced94f7b159a44d28ff' ||
      reviewedReactSupplement.publishedGitHead !== 'b3b1287aad81def2e2ae707274b74531b61ddbaf' ||
      reviewedReactSupplement.sourceCommit === reviewedReactSupplement.publishedGitHead ||
      !reviewedReactSupplement.tarballComparison?.unresolvedPublishedGitHead ||
      reviewedReactInventory?.supplementalLicenseText?.status !== 'reviewed' ||
      actualFrontendInventory.packagesWithUnreviewedLicenseSupplement.includes('react-remove-scroll-bar@2.3.8')) {
    throw new Error('reviewed npm tarball comparison was not propagated as evidence while retaining the unresolved published gitHead limitation');
  }
  for (const alter of ['version', 'integrity', 'tarball-version', 'tarball-integrity', 'githead']) {
    const changedCatalog = structuredClone(actualSupplements);
    const supplement = changedCatalog.supplements.find((item) => item.name === 'react-remove-scroll-bar' && item.version === '2.3.8');
    if (alter === 'version') supplement.version = '2.3.9';
    if (alter === 'integrity') supplement.npmIntegrity = 'sha512-stale';
    if (alter === 'tarball-version') supplement.tarballComparison.publishedVersion = '2.3.9';
    if (alter === 'tarball-integrity') supplement.tarballComparison.publishedIntegrity = 'sha512-stale';
    if (alter === 'githead') supplement.publishedGitHead = supplement.sourceCommit;
    let rejected = false;
    try { validateFrontendSupplementLock({ lockText: releaseLock, catalog: changedCatalog, inventory: actualFrontendInventory }); } catch { rejected = true; }
    if (!rejected) throw new Error(`reviewed react-remove-scroll-bar evidence was accepted after ${alter} drift`);
  }
  let staleSupplementRejected = false;
  try {
    validateFrontendSupplementLock({ lockText: releaseLock.replace(actualSupplements.supplements[0].npmIntegrity, 'sha512-stale'), catalog: actualSupplements, inventory: actualFrontendInventory });
  } catch { staleSupplementRejected = true; }
  if (!staleSupplementRejected) throw new Error('supplemental license metadata was accepted after npm lock integrity drift');
  for (const name of ['linux', 'wsl', 'sidecar-a', 'sidecar-b', 'base']) {
    await writeFile(path.join(actualSupplementOutput, `kautuka-${name}.spdx.json`), JSON.stringify({ packages: [], hasExtractedLicensingInfos: [] }));
  }
  const creditsFixture = path.join(temp, 'reviewed-credits-fixture');
  const creditsLicenseDir = path.join(creditsFixture, 'licenses');
  await mkdir(creditsLicenseDir, { recursive: true });
  const packageText = 'MIT License\nCopyright (c) Reviewed fixture package author\nFixture notice text retained.\n';
  const packageTextHash = createHash('sha256').update(packageText).digest('hex');
  const packageTextName = `LICENSE-NPM-${packageTextHash}.txt`;
  await writeFile(path.join(creditsLicenseDir, packageTextName), packageText);
  const fixtureInventory = {
    schemaVersion: 1,
    packageCount: 1,
    packages: [{
      name: 'reviewed-frontend-widget',
      version: '1.4.2',
      licenseExpression: 'MIT',
      source: 'https://github.com/example/reviewed-frontend-widget',
      licenseFiles: [{ file: packageTextName, sha256: packageTextHash }],
      supplementalLicenseText: null,
    }],
    packagesMissingPackageSpecificLicenseText: [],
    packagesMissingLicenseTextEvidence: [],
    packagesWithUnreviewedLicenseSupplement: [],
  };
  await writeFile(path.join(creditsFixture, 'FRONTEND_DEPENDENCIES.json'), `${JSON.stringify(fixtureInventory, null, 2)}\n`);
  await writeFile(path.join(creditsFixture, 'THIRD_PARTY_COMPONENTS.json'), JSON.stringify({ releaseTag: 'fixture-v1', components: [] }));
  await writeFile(path.join(creditsFixture, 'THIRD_PARTY_NOTICES.txt'), 'Fixture notices header\n');
  const extractedMitText = 'MIT License\nSPDX extracted fixture license text.\n';
  for (const name of ['linux', 'wsl', 'sidecar-a', 'sidecar-b', 'base']) {
    await writeFile(path.join(creditsFixture, `kautuka-${name}.spdx.json`), JSON.stringify({
      packages: [],
      hasExtractedLicensingInfos: name === 'linux' ? [{ licenseId: 'MIT', extractedText: extractedMitText }] : [],
    }));
  }
  const offlineFetchGuard = path.join(temp, 'deny-network.cjs');
  await writeFile(offlineFetchGuard, "globalThis.fetch = async () => { throw new Error('credits fixture attempted network access'); };\n");
  const creditsArgs = ['--require', offlineFetchGuard, path.join(scriptDir, 'generate-release-credits.mjs'), '--directory', creditsFixture];
  const creditsSuccess = spawnSync(process.execPath, creditsArgs, { cwd: root, encoding: 'utf8' });
  if (creditsSuccess.status !== 0) throw new Error(`offline reviewed release credits failed: ${creditsSuccess.stderr}${creditsSuccess.stdout}`);
  const creditsText = await readFile(path.join(creditsFixture, 'CREDITS.md'), 'utf8');
  const noticesText = await readFile(path.join(creditsFixture, 'THIRD_PARTY_NOTICES.txt'), 'utf8');
  const generatedMitText = await readFile(path.join(creditsLicenseDir, 'LICENSE-MIT.txt'), 'utf8');
  if (!creditsText.includes('reviewed-frontend-widget | 1.4.2 | MIT | https://github.com/example/reviewed-frontend-widget') ||
      !noticesText.includes('reviewed-frontend-widget') || generatedMitText !== extractedMitText ||
      !(await readFile(path.join(creditsLicenseDir, packageTextName), 'utf8')).includes('Reviewed fixture package author')) {
    throw new Error('release credits did not preserve reviewed frontend metadata, SPDX extracted text, or package-specific copyright/notice text');
  }

  const unreviewedCreditsFixture = path.join(temp, 'unreviewed-credits-fixture');
  await cp(creditsFixture, unreviewedCreditsFixture, { recursive: true });
  const unreviewedInventoryPath = path.join(unreviewedCreditsFixture, 'FRONTEND_DEPENDENCIES.json');
  const unreviewedInventory = JSON.parse(await readFile(unreviewedInventoryPath, 'utf8'));
  unreviewedInventory.packagesWithUnreviewedLicenseSupplement = ['synthetic-unreviewed-widget@9.9.9'];
  await writeFile(unreviewedInventoryPath, `${JSON.stringify(unreviewedInventory, null, 2)}\n`);
  const unreviewedCredits = spawnSync(process.execPath, [path.join(scriptDir, 'generate-release-credits.mjs'), '--directory', unreviewedCreditsFixture], { cwd: root, encoding: 'utf8' });
  if (unreviewedCredits.status === 0 || !`${unreviewedCredits.stderr}${unreviewedCredits.stdout}`.includes('synthetic-unreviewed-widget@9.9.9')) {
    throw new Error('release credits did not block a synthetic unreviewed frontend license supplement');
  }
  await writeFile(path.join(creditsLicenseDir, packageTextName), `${packageText}tampered\n`);
  const tamperedCredits = spawnSync(process.execPath, creditsArgs, { cwd: root, encoding: 'utf8' });
  if (tamperedCredits.status === 0 || !`${tamperedCredits.stderr}${tamperedCredits.stdout}`.includes('license text hash mismatch')) {
    throw new Error('release credits accepted package-specific frontend license text after its hash changed');
  }

  for (const badLock of [
    releaseLock.replace('specifier: ^0.12.0', 'specifier: ^0.12.1'),
    releaseLock.replace(releaseCatalog.components.find((item) => item.id === 'elkjs').npmIntegrity, 'sha512-stale'),
    releaseLock.replace('elkjs: 0.9.3', 'elkjs: 0.9.4'),
  ]) {
    let rejected = false;
    try { validateFrontendLicenseCatalog({ lockText: badLock, catalog: releaseCatalog, inventory: releaseInventory }); } catch { rejected = true; }
    if (!rejected) throw new Error('frontend lock drift did not invalidate the reviewed elkjs catalog evidence');
  }

  list[0].dependencies['parent-a'].dependencies.elkjs.version = '0.12.1';
  await writeFile(listPath, `${JSON.stringify(list, null, 2)}\n`);
  const stale = run(staleOutput);
  if (stale.status === 0 || !`${stale.stderr}${stale.stdout}`.includes('does not match pnpm')) {
    throw new Error('inventory generation accepted stale installed package metadata');
  }
  list[0].dependencies['parent-a'].dependencies.elkjs.version = '0.12.0';
  list[0].dependencies['parent-a'].dependencies.elkjs.dependencies['missing-required'] = { version: '1.0.0', path: path.join(temp, 'missing-required') };
  await writeFile(path.join(elkjsPeerARoot, 'package.json'), JSON.stringify({ ...elkjsManifest, dependencies: { 'missing-required': '1.0.0' } }));
  await writeFile(listPath, `${JSON.stringify(list, null, 2)}\n`);
  const missingRequired = run(path.join(temp, 'missing-required-output'));
  if (missingRequired.status === 0 || !`${missingRequired.stderr}${missingRequired.stdout}`.includes('Required installed frontend dependency is missing')) {
    throw new Error('inventory generation silently skipped a missing required frontend dependency');
  }
  console.log('frontend inventory traversed peer contexts, verified supplemental hashes/SRI and reviewed tarball evidence, retained copyright/notice text, rejected stale/missing packages and synthetic unreviewed credits');
} finally {
  await rm(temp, { recursive: true, force: true });
}
