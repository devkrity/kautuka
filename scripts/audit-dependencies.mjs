#!/usr/bin/env node
/**
 * Fail release verification when pnpm audit reports critical/high vulnerabilities.
 * Documented exceptions require an ADR-linked expiry and owner; do not suppress silently.
 */
import { execFileSync, execSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/** ADR-linked exceptions only. Empty until an owner documents a time-bounded waiver. */
const DOCUMENTED_EXCEPTIONS = [{
  module: 'picomatch',
  advisoryId: 'GHSA-fc5q-39xc-vjpf',
  expiresOn: '2026-12-19',
  owner: 'Kautuka maintainers',
  target: 'frontend-workspace',
  version: '4.0.7',
  patchHash: 'e9a097c6b00c85b797fcc3887f2306f83d950fd01083e859841088fde3dd1843',
}];

const AUDIT_TARGETS = [
  { label: 'repo-root', cwd: repoRoot },
  { label: 'frontend-workspace', cwd: path.join(repoRoot, 'src/frontend') },
];

function runAudit(cwd) {
  const command = process.platform === 'win32'
    ? 'npx --yes pnpm@11.5.2 audit --audit-level=high --json'
    : 'pnpm audit --audit-level=high --json';
  return execSync(command, {
    cwd,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  });
}

function parseAudit(stdout) {
  try {
    return JSON.parse(stdout);
  } catch {
    return null;
  }
}

function collectFindings(report) {
  const advisories = report?.advisories ?? {};
  return Object.values(advisories).map((entry) => ({
    id: entry.github_advisory_id ?? entry.cwe ?? entry.id,
    severity: entry.severity,
    module: entry.module_name,
    title: entry.title,
    url: entry.url,
    findings: entry.findings ?? [],
  }));
}

// The exception applies only to the audited dev-only version with the reviewed
// patch in the frozen lockfile. It never accepts an unpatched or expired finding.
function isVerifiedException(exception, target, finding) {
  if (target.label !== exception.target || new Date().toISOString().slice(0, 10) > exception.expiresOn
      || finding.findings.length === 0
      || finding.findings.some((item) => item.version !== exception.version || item.dev !== true)) return false;
  try {
    const patch = readFileSync(path.join(target.cwd, 'patches', `picomatch@${exception.version}.patch`));
    const lock = readFileSync(path.join(target.cwd, 'pnpm-lock.yaml'), 'utf8');
    const workspace = readFileSync(path.join(target.cwd, 'pnpm-workspace.yaml'), 'utf8');
    const hash = createHash('sha256').update(patch).digest('hex');
    const snapshots = lock.split('\nsnapshots:\n')[1] ?? '';
    if (hash !== exception.patchHash
        || !workspace.includes(`picomatch@${exception.version}: patches/picomatch@${exception.version}.patch`)
        || !lock.includes(`picomatch@${exception.version}: ${hash}`)
        || !snapshots.includes(`picomatch@${exception.version}(patch_hash=${hash})`)
        || /picomatch: 4\.0\.7(?!\(patch_hash=)/.test(snapshots)) return false;
    const env = { ...process.env };
    delete env.PICOMATCH_TEST_PATH;
    execFileSync(process.execPath, [path.join(repoRoot, 'scripts/test-picomatch-patch.mjs')], { env, timeout: 20000, stdio: 'pipe' });
    return true;
  } catch {
    return false;
  }
}

const failures = [];

for (const target of AUDIT_TARGETS) {
  let stdout = '';
  try {
    stdout = runAudit(target.cwd);
  } catch (error) {
    stdout = error.stdout?.toString?.() ?? '';
    if (!stdout) {
      failures.push(`${target.label}: audit command failed (${error.message ?? error})`);
      continue;
    }
  }

  const report = parseAudit(stdout);
  if (!report) {
    failures.push(`${target.label}: unable to parse pnpm audit output`);
    continue;
  }

  const findings = collectFindings(report).filter(
    (finding) => finding.severity === 'critical' || finding.severity === 'high',
  );

  for (const finding of findings) {
    const exception = DOCUMENTED_EXCEPTIONS.find(
      (item) => item.module === finding.module && item.advisoryId === finding.id,
    );
    if (exception && isVerifiedException(exception, target, finding)) {
      console.log(
        `Accepted documented exception for ${finding.module} (${finding.id}) until ${exception.expiresOn}.`,
      );
      continue;
    }
    failures.push(
      `${target.label}: ${finding.severity} ${finding.module} — ${finding.title} (${finding.url ?? finding.id})`,
    );
  }
}

if (failures.length > 0) {
  console.error('Dependency audit release gate failed:');
  for (const failure of failures) {
    console.error(`- ${failure}`);
  }
  console.error(
    'Policy: upgrade dependencies or add a time-bounded documented exception in scripts/audit-dependencies.mjs.',
  );
  process.exit(1);
}

console.log(
  `Dependency audit release gate passed (${AUDIT_TARGETS.length} workspaces, ${DOCUMENTED_EXCEPTIONS.length} documented exceptions).`,
);
