#!/usr/bin/env node
import { randomBytes } from "node:crypto";
import { spawnSync } from "node:child_process";
import { copyFile, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const sourceScanner = path.join(scriptDirectory, "run-precommit-secret-scan.mjs");
const gitleaks = process.env.KAUTUKA_GITLEAKS_BIN;
if (!gitleaks) throw new Error("Set KAUTUKA_GITLEAKS_BIN to the pinned Gitleaks 8.30.1 executable to run this hermetic regression.");

const temp = await mkdtemp(path.join(os.tmpdir(), "kautuka-precommit-secret-test-"));
const canary = `ghp_${randomBytes(27).toString("base64url")}`;
const configPath = path.join(temp, "gitleaks-test.toml");
const env = { ...process.env, KAUTUKA_GITLEAKS_BIN: gitleaks, GITLEAKS_CONFIG: configPath };

function command(file, args, cwd, options = {}) {
  return spawnSync(file, args, { cwd, env, encoding: "utf8", ...options });
}

function git(args, cwd) {
  const result = command("git", args, cwd);
  if (result.status !== 0) throw new Error(`Git fixture setup failed: ${result.stderr || result.stdout}`);
}

function assertDetectedWithoutDisclosure(result, label) {
  const output = `${result.stdout ?? ""}${result.stderr ?? ""}`;
  if (result.status === 0) throw new Error(`${label}: staged synthetic credential was not rejected. Scanner output: ${result.stderr || result.stdout || "<empty>"}`);
  if (output.includes(canary)) throw new Error(`${label}: Gitleaks output disclosed the synthetic credential despite redaction.`);
  if (!/Gitleaks found [1-9][0-9]* staged secret finding\(s\)\./.test(output)) throw new Error(`${label}: Gitleaks did not report a positive finding count; scanner or Git errors must not count as detection.`);
}

async function installScannerFixture(repository) {
  const scripts = path.join(repository, "scripts");
  await mkdir(scripts, { recursive: true });
  await copyFile(sourceScanner, path.join(scripts, "run-precommit-secret-scan.mjs"));
}

function scan(repository) {
  return command(process.execPath, [path.join(repository, "scripts", "run-precommit-secret-scan.mjs")], repository);
}

function scanWithScanner(repository, scanner) {
  return command(process.execPath, [path.join(repository, "scripts", "run-precommit-secret-scan.mjs")], repository, {
    env: { ...env, KAUTUKA_GITLEAKS_BIN: scanner },
  });
}

const existingStagingDirectories = new Set(
  (await readdir(os.tmpdir())).filter((name) => name.startsWith("kautuka-staged-secrets-")),
);

try {
  // A minimal deterministic rule avoids the upstream default's report-filename allowlist.
  await writeFile(configPath, `title = "Kautuka scanner regression"\n[[rules]]\nid = "synthetic-github-token"\ndescription = "Synthetic test token"\nregex = '''ghp_[A-Za-z0-9_-]{30,}'''\nsecretGroup = 0\n`);
  const ordinary = path.join(temp, "ordinary-repository");
  await mkdir(ordinary, { recursive: true });
  git(["init", "--quiet", "--initial-branch=main"], ordinary);
  git(["config", "user.name", "Kautuka Secret Scan Test"], ordinary);
  git(["config", "user.email", "secret-scan-test@example.invalid"], ordinary);
  await installScannerFixture(ordinary);
  await writeFile(path.join(ordinary, "safe.txt"), "safe fixture\n");
  git(["add", "safe.txt", "scripts/run-precommit-secret-scan.mjs"], ordinary);
  git(["commit", "--quiet", "-m", "fixture baseline"], ordinary);

  // This path formerly collided with Gitleaks' report output inside the scan root.
  await writeFile(path.join(ordinary, "gitleaks-report.json"), `GH_TOKEN=${canary}\n`);
  git(["add", "gitleaks-report.json"], ordinary);
  assertDetectedWithoutDisclosure(scan(ordinary), "ordinary repository report-name collision");

  git(["rm", "--cached", "--quiet", "gitleaks-report.json"], ordinary);
  const unstagedOnly = scan(ordinary);
  if (unstagedOnly.status !== 0) throw new Error(`ordinary repository: an unstaged-only credential affected the staged-content scan: ${unstagedOnly.stderr || unstagedOnly.stdout}`);
  const scannerFailure = scanWithScanner(ordinary, path.join(temp, "missing-gitleaks.exe"));
  if (scannerFailure.status === 0 || !`${scannerFailure.stderr}${scannerFailure.stdout}`.includes("configured Gitleaks executable")) {
    throw new Error("pre-commit secret scan silently accepted an unavailable Gitleaks executable");
  }

  const nonRepository = path.join(temp, "non-git-directory");
  await mkdir(nonRepository, { recursive: true });
  await installScannerFixture(nonRepository);
  const gitFailure = scan(nonRepository);
  if (gitFailure.status === 0 || !`${gitFailure.stderr}${gitFailure.stdout}`.includes("Could not inspect the staged Git index")) {
    throw new Error("pre-commit secret scan silently accepted a Git index error");
  }

  const linked = path.join(temp, "linked-worktree");
  git(["worktree", "add", "--quiet", "--detach", linked, "HEAD"], ordinary);
  await installScannerFixture(linked);
  await writeFile(path.join(linked, "staged-credential.txt"), `GH_TOKEN=${canary}\n`);
  git(["add", "staged-credential.txt", "scripts/run-precommit-secret-scan.mjs"], linked);
  assertDetectedWithoutDisclosure(scan(linked), "linked worktree");

  const ordinaryFiles = await readFile(path.join(ordinary, "safe.txt"), "utf8");
  if (!ordinaryFiles.includes("safe fixture")) throw new Error("ordinary repository fixture was unexpectedly modified");
  const remainingStagingDirectories = (await readdir(os.tmpdir())).filter((name) =>
    name.startsWith("kautuka-staged-secrets-") && !existingStagingDirectories.has(name),
  );
  if (remainingStagingDirectories.length > 0) throw new Error("pre-commit scan left a staged-content temporary export behind");
  console.log("pre-commit scan found and redacted staged credentials in ordinary and linked worktrees, ignored unstaged-only credentials, failed closed on scanner/Git errors, and cleaned temporary exports");
} finally {
  await rm(temp, { recursive: true, force: true });
}
