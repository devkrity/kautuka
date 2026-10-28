#!/usr/bin/env node
/**
 * Fail release verification when pnpm audit reports critical/high vulnerabilities.
 * Documented exceptions require an ADR-linked expiry and owner; do not suppress silently.
 */
import { execSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

/** ADR-linked exceptions only. Empty until an owner documents a time-bounded waiver. */
const DOCUMENTED_EXCEPTIONS = [];

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
  }));
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
    if (exception) {
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
