import { chmodSync, copyFileSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";

const HOOK_FILES = [
  "kautuka_hook.py",
  "kautuka_workload.nft.json.tmpl",
];
const SAFE_HOOK_PATH = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

function tomlString(value) {
  return JSON.stringify(value);
}

function effectiveHome(env) {
  return path.resolve(env.HOME?.length ? env.HOME : os.homedir());
}

function effectiveXdgStateHome(env, managerWorkingDirectory) {
  const configured = env.XDG_STATE_HOME;
  if (configured?.length) return path.resolve(managerWorkingDirectory, configured);
  return path.join(effectiveHome(env), ".local", "state");
}

/** Stage the Linux development-only OCI hook and return a Manager-only environment. */
export function stageLinuxDevelopmentNacHook(repoRoot, inheritedEnv = process.env, options = {}) {
  const existingOverride = inheritedEnv.CONTAINERS_CONF_OVERRIDE;
  if (existingOverride?.trim()) {
    throw new Error(
      "CONTAINERS_CONF_OVERRIDE is already set. The Linux development launcher needs this Podman override for its temporary NAC hook; unset it and retry.",
    );
  }

  const nacSource = path.join(repoRoot, "scripts", "nac");
  const missing = HOOK_FILES.filter((name) => !readableFile(path.join(nacSource, name)));
  if (missing.length) {
    throw new Error(`Linux development NAC hook payload is incomplete: ${missing.join(", ")}`);
  }

  const tempRoot = options.tempRoot ?? os.tmpdir();
  const runDirectory = mkdtempSync(path.join(tempRoot, `kautuka-dev-nac-${process.getuid?.() ?? "user"}-`));
  chmodSync(runDirectory, 0o700);

  try {
    const hooksDirectory = path.join(runDirectory, "hooks.d");
    mkdirSync(hooksDirectory, { mode: 0o700 });
    const hookScript = path.join(runDirectory, "kautuka_hook.py");
    const hookTemplate = path.join(runDirectory, "kautuka_workload.nft.json.tmpl");
    copyFileSync(path.join(nacSource, HOOK_FILES[0]), hookScript);
    copyFileSync(path.join(nacSource, HOOK_FILES[1]), hookTemplate);
    chmodSync(hookScript, 0o700);
    chmodSync(hookTemplate, 0o600);

    const home = effectiveHome(inheritedEnv);
    const managerWorkingDirectory = path.resolve(options.managerWorkingDirectory ?? repoRoot);
    const xdgStateHome = effectiveXdgStateHome(inheritedEnv, managerWorkingDirectory);
    const hookJson = {
      version: "1.0.0",
      hook: {
        path: hookScript,
        args: ["kautuka_hook.py"],
        env: [
          `PATH=${SAFE_HOOK_PATH}`,
          `HOME=${home}`,
          `XDG_STATE_HOME=${xdgStateHome}`,
        ],
      },
      when: { always: true },
      stages: ["createRuntime"],
    };
    const hookConfigPath = path.join(hooksDirectory, `kautuka-dev-${path.basename(runDirectory)}.json`);
    writeFileSync(hookConfigPath, `${JSON.stringify(hookJson, null, 2)}\n`, { mode: 0o600, flag: "wx" });
    const overridePath = path.join(runDirectory, "containers.conf");
    // containers/common append mode preserves the user's configured hook directories.
    writeFileSync(
      overridePath,
      `[engine]\nhooks_dir = [${tomlString(hooksDirectory)}, { append = true }]\n`,
      { mode: 0o600, flag: "wx" },
    );
    chmodSync(overridePath, 0o600);

    return {
      directory: runDirectory,
      managerEnvironment: {
        ...inheritedEnv,
        XDG_STATE_HOME: xdgStateHome,
        CONTAINERS_CONF_OVERRIDE: overridePath,
      },
    };
  } catch (error) {
    rmSync(runDirectory, { recursive: true, force: true });
    throw error;
  }
}

function readableFile(filePath) {
  try {
    readFileSync(filePath);
    return true;
  } catch {
    return false;
  }
}

export function removeLinuxDevelopmentNacHook(directory) {
  if (!directory) return;
  rmSync(directory, { recursive: true, force: true });
}

export function canRemoveLinuxDevelopmentNacHook(stoppedProcessGroups) {
  return stoppedProcessGroups.every(Boolean);
}
