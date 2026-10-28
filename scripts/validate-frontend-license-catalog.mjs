export function validateFrontendLicenseCatalog({ lockText, catalog, inventory }) {
  const elkjs = catalog.components?.find((item) => item.id === 'elkjs');
  if (!elkjs || !elkjs.version || !elkjs.npmSpecifier || !elkjs.npmIntegrity ||
      !/^[a-f0-9]{40}$/.test(elkjs.sourceCommit ?? '') || !elkjs.source) {
    throw new Error('The reviewed elkjs catalog record is incomplete.');
  }
  const packageRecord = inventory.packages?.find((item) => item.name === 'elkjs' && item.version === elkjs.version);
  if (!packageRecord || packageRecord.licenseExpression !== elkjs.licenseExpression) {
    throw new Error('The reviewed elkjs source/license record does not match an installed frontend production dependency.');
  }
  assertLockedPackage(lockText, 'elkjs', elkjs.version, elkjs.npmIntegrity);
  const webImporter = lockText.match(/^  web:\r?\n([\s\S]*?)(?=^  [^\s]|^packages:)/m)?.[1] ?? '';
  if (!webImporter.includes(`      elkjs:\n        specifier: ${elkjs.npmSpecifier}\n        version: ${elkjs.version}\n`)) {
    throw new Error('The frontend pnpm lock no longer matches the reviewed elkjs version, direct dependency, and npm integrity.');
  }
  const mermaidElkjs = catalog.components?.find((item) => item.id === 'elkjs-mermaid');
  const mermaidPackage = inventory.packages?.find((item) => item.name === 'elkjs' && item.version === mermaidElkjs?.version);
  if (!mermaidElkjs || !mermaidPackage || mermaidPackage.licenseExpression !== mermaidElkjs.licenseExpression ||
      !mermaidElkjs.sourceCommit || !mermaidElkjs.source) {
    throw new Error('The reviewed transitive elkjs source/license record does not match an installed frontend production dependency.');
  }
  assertLockedPackage(lockText, 'elkjs', mermaidElkjs.version, mermaidElkjs.npmIntegrity);
  const snapshots = lockText.split(/\r?\nsnapshots:\r?\n/)[1] ?? '';
  const mermaidSnapshot = snapshots.match(/^  mermaid@[^\n]+:\r?\n([\s\S]*?)(?=^  [^ ]|$(?![\s\S]))/m)?.[1] ?? '';
  if (!mermaidSnapshot.includes(`elkjs: ${mermaidElkjs.version}`)) {
    throw new Error('The frontend lock no longer ties Mermaid to the reviewed transitive elkjs version.');
  }
  return true;
}

export function validateFrontendSupplementLock({ lockText, catalog, inventory }) {
  const packages = new Map((inventory.packages ?? []).map((item) => [`${item.name}@${item.version}`, item]));
  for (const supplement of catalog.supplements ?? []) {
    const key = `${supplement.name}@${supplement.version}`;
    const record = packages.get(key);
    if (!record || record.licenseExpression !== supplement.licenseExpression || !supplement.npmIntegrity ||
        !supplement.sourceUrl?.includes(supplement.sourceCommit)) {
      throw new Error(`Supplemental frontend license record is stale or does not match the installed package: ${key}`);
    }
    const comparison = supplement.tarballComparison;
    if (comparison && (
      comparison.publishedVersion !== supplement.version ||
      comparison.sourceVersion !== '2.3.7' ||
      comparison.publishedIntegrity !== supplement.npmIntegrity ||
      !/^sha512-[A-Za-z0-9+/]+=*$/.test(comparison.sourceIntegrity ?? '') ||
      !/^sha512-[A-Za-z0-9+/]+=*$/.test(comparison.publishedIntegrity ?? '') ||
      comparison.totalFiles !== 27 ||
      comparison.identicalFiles !== 26 ||
      !/^[a-f0-9]{64}$/.test(comparison.readmeSha256 ?? '') ||
      comparison.onlyChangedFile?.path !== 'package/package.json' ||
      !/^[a-f0-9]{64}$/.test(comparison.onlyChangedFile.sourceSha256 ?? '') ||
      !/^[a-f0-9]{64}$/.test(comparison.onlyChangedFile.publishedSha256 ?? '') ||
      JSON.stringify(comparison.onlyChangedFile.changedFields) !== JSON.stringify(['version', 'dependencies.react-style-singleton']) ||
      comparison.unresolvedPublishedGitHead !== true ||
      !/^[a-f0-9]{40}$/.test(supplement.publishedGitHead ?? '') ||
      supplement.publishedGitHead === supplement.sourceCommit
    )) {
      throw new Error(`Supplemental frontend tarball evidence is stale or incomplete: ${key}`);
    }
    assertLockedPackage(lockText, supplement.name, supplement.version, supplement.npmIntegrity);
  }
  return true;
}

function assertLockedPackage(lockText, name, version, integrity) {
  if (!integrity) throw new Error(`The reviewed npm integrity is missing for ${name}@${version}.`);
  const packageBlock = lockText.match(new RegExp(`^  ${escapeRegex(name)}@${escapeRegex(version)}:\\r?\\n([\\s\\S]*?)(?=^  [^ ]|^snapshots:)`, 'm'))?.[1] ?? '';
  if (!packageBlock.includes(`integrity: ${integrity}`)) {
    throw new Error(`The frontend pnpm lock no longer matches the reviewed ${name}@${version} npm integrity.`);
  }
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
