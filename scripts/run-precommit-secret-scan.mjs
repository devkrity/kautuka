import { mkdir, mkdtemp, readFile, rm } from "node:fs/promises";
import { spawnSync } from "node:child_process";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDirectory, "..");
const gitleaksVersion = "8.30.1";

function run(command, args, options = {}) {
  return spawnSync(command, args, {
    cwd: repositoryRoot,
    encoding: "utf8",
    ...options,
  });
}

function windowsDrivePathToWsl(windowsPath) {
  const match = /^([A-Za-z]):[\\/](.*)$/.exec(windowsPath);
  if (!match) return null;
  return `/mnt/${match[1].toLowerCase()}/${match[2].replaceAll("\\", "/")}`;
}

function requireSuccess(label, result) {
  if (result.error) throw new Error(`${label}: ${result.error.message}`);
  if (result.status !== 0) throw new Error(`${label} exited with status ${result.status ?? "unknown"}.`);
}

const temporaryRoot = await mkdtemp(path.join(os.tmpdir(), "kautuka-staged-secrets-"));
const stagedDirectory = path.join(temporaryRoot, "staged");
const stagedRoot = `${stagedDirectory}${path.sep}`;
const reportPath = path.join(temporaryRoot, "report.json");
const containerReportPath = "/scan/report.json";
let failureMessage;

try {
  await mkdir(stagedDirectory, { recursive: true });
  const unmerged = run("git", ["ls-files", "--unmerged", "-z"]);
  requireSuccess("Could not inspect the staged Git index", unmerged);
  if (unmerged.stdout.length > 0) {
    throw new Error("Pre-commit secret scan cannot run while the Git index contains unresolved conflicts.");
  }

  const exportIndex = run("git", ["checkout-index", "--all", `--prefix=${stagedRoot}`]);
  requireSuccess("Could not materialize the staged Git index", exportIndex);

  const localScanner = process.env.KAUTUKA_GITLEAKS_BIN;
  let scan;
  if (localScanner) {
    const version = run(localScanner, ["version"], { cwd: temporaryRoot });
    requireSuccess("Could not verify the configured Gitleaks executable", version);
    if (version.stdout.trim() !== gitleaksVersion) {
      throw new Error(`Pre-commit secret scan requires Gitleaks ${gitleaksVersion}; configured executable reports ${version.stdout.trim() || "no version"}.`);
    }

    scan = run(localScanner, [
      "dir", "--no-banner", "--redact=100", "--report-format", "json", "--report-path", reportPath,
      "--exit-code", "1", stagedRoot,
    ], {
      cwd: temporaryRoot,
      stdio: "inherit",
    });
  } else {
    const volumeSuffix = process.platform === "linux" ? ":z" : "";
    const gitleaksArgs = [
      "run", "--rm", "-v", `${temporaryRoot}:/scan${volumeSuffix}`,
      `ghcr.io/gitleaks/gitleaks:v${gitleaksVersion}`,
      "dir", "--no-banner", "--redact=100", "--report-format", "json", "--report-path", containerReportPath,
      "--exit-code", "1", "/scan/staged",
    ];
    scan = run("podman", gitleaksArgs, { stdio: "inherit" });
    if (scan.error?.code === "ENOENT" && process.platform === "win32") {
      const wslRoot = windowsDrivePathToWsl(temporaryRoot);
      if (!wslRoot) {
        throw new Error("Cannot map the staged-content directory into WSL for the pre-commit secret scan.");
      }
      scan = run("wsl.exe", [
        "--", "podman", "run", "--rm", "-v", `${wslRoot}:/scan:z`,
        `ghcr.io/gitleaks/gitleaks:v${gitleaksVersion}`,
        "dir", "--no-banner", "--redact=100", "--report-format", "json", "--report-path", containerReportPath,
        "--exit-code", "1", "/scan/staged",
      ], { stdio: "inherit" });
    }

    if (scan.error?.code === "ENOENT") {
      throw new Error("Pre-commit secret scan requires Podman on PATH or a WSL Podman installation on Windows. Configure KAUTUKA_GITLEAKS_BIN only with the pinned Gitleaks 8.30.1 executable.");
    }
  }

  if (scan.error) throw new Error(`Staged-content Gitleaks scan could not start: ${scan.error.message}`);
  let findings;
  try {
    findings = JSON.parse(await readFile(reportPath, "utf8"));
  } catch {
    throw new Error("Staged-content Gitleaks scan did not produce a valid JSON report.");
  }
  if (!Array.isArray(findings)) throw new Error("Staged-content Gitleaks report has an unexpected format.");
  if (scan.status === 1 && findings.length > 0) {
    throw new Error(`Gitleaks found ${findings.length} staged secret finding(s).`);
  }
  if (scan.status !== 0) throw new Error(`Staged-content Gitleaks scan exited with status ${scan.status ?? "unknown"} without a valid finding report.`);
  if (findings.length > 0) throw new Error(`Gitleaks reported ${findings.length} staged finding(s) with a successful exit status.`);
  console.log(`Staged-content Gitleaks scan passed (v${gitleaksVersion}).`);
} catch (error) {
  failureMessage = error instanceof Error ? error.message : String(error);
} finally {
  await rm(temporaryRoot, { recursive: true, force: true });
}

if (failureMessage) {
  console.error(failureMessage);
  process.exitCode = 1;
}
