import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { EventEmitter } from "node:events";
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { canRemoveLinuxDevelopmentNacHook, removeLinuxDevelopmentNacHook, stageLinuxDevelopmentNacHook } from "./launcher-nac-hook.mjs";
import { killProcessTree } from "./launcher-process-tree.mjs";

// Shutdown result regression runs on every host, including Windows.
assert.equal(await killProcessTree({ pid: undefined }, { platform: "win32" }), true);
async function fakeTaskkill(exitCode, child = { pid: 123, exitCode: null, signalCode: null }) {
  return killProcessTree(child, {
    platform: "win32",
    spawnProcess: () => {
      const processStub = new EventEmitter();
      queueMicrotask(() => processStub.emit("exit", exitCode));
      return processStub;
    },
  });
}
assert.equal(await fakeTaskkill(0), true);
assert.equal(await fakeTaskkill(1), false);
assert.equal(await fakeTaskkill(1, { pid: 123, exitCode: 0, signalCode: null }), true);
assert.equal(canRemoveLinuxDevelopmentNacHook([true, true]), true);
assert.equal(canRemoveLinuxDevelopmentNacHook([true, false]), false);
process.stdout.write("Launcher process-tree result checks passed.\n");

if (process.platform === "linux") {
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const inheritedEnv = {
  HOME: "/tmp/kautuka-test-home",
  XDG_STATE_HOME: "/tmp/kautuka-test-state",
  PATH: "/usr/bin",
  API_TOKEN: "must-not-enter-hook-config",
};

let staged;
try {
  staged = stageLinuxDevelopmentNacHook(repositoryRoot, inheritedEnv);
  const stagedRoot = staged.directory;
  const hooksDirectory = path.join(stagedRoot, "hooks.d");
  const hookScript = path.join(stagedRoot, "kautuka_hook.py");
  const hookTemplate = path.join(stagedRoot, "kautuka_workload.nft.json.tmpl");
  const configName = readdirSync(hooksDirectory).find((name) => name.endsWith(".json"));
  assert.match(configName ?? "", /^kautuka-dev-kautuka-dev-nac-/);
  const hookConfigPath = path.join(hooksDirectory, configName);
  const hookJson = JSON.parse(readFileSync(hookConfigPath, "utf8"));
  const managerEnv = staged.managerEnvironment;

  // Fail-first regression: the private OCI hook is present in a native Podman
  // hooks_dir append override even when no user-installed Kautuka hook exists.
  assert.ok(existsSync(hookScript), "staged development hook script exists");
  assert.ok(existsSync(hookTemplate), "adjacent nft template exists");
  assert.equal(hookJson.hook.path, hookScript);
  assert.deepEqual(hookJson.hook.args, ["kautuka_hook.py"]);
  assert.deepEqual(hookJson.stages, ["createRuntime"]);
  assert.deepEqual(hookJson.when, { always: true });
  const configText = readFileSync(managerEnv.CONTAINERS_CONF_OVERRIDE, "utf8");
  assert.match(configText, /^\[engine\]\s*$/m);
  assert.match(configText, /hooks_dir\s*=\s*\[.*\{\s*append\s*=\s*true\s*\}\]/);
  assert.ok(configText.includes(hooksDirectory));
  assert.notEqual(configName, "kautuka_hook.json", "the temporary config has a distinct name and cannot replace another file by name");

  // Scope regression: only the Manager-specific env receives the override.
  assert.equal(inheritedEnv.CONTAINERS_CONF_OVERRIDE, undefined);
  assert.equal(managerEnv.API_TOKEN, inheritedEnv.API_TOKEN);
  assert.equal(managerEnv.XDG_STATE_HOME, inheritedEnv.XDG_STATE_HOME);
  const hookEnvironment = Object.fromEntries(hookJson.hook.env.map((entry) => {
    const separator = entry.indexOf("=");
    return [entry.slice(0, separator), entry.slice(separator + 1)];
  }));
  assert.deepEqual(Object.keys(hookEnvironment).sort(), ["HOME", "PATH", "XDG_STATE_HOME"]);
  assert.equal(hookEnvironment.PATH, "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin");
  assert.equal(hookEnvironment.HOME, inheritedEnv.HOME);
  assert.equal(hookEnvironment.XDG_STATE_HOME, managerEnv.XDG_STATE_HOME);
  assert.ok(!readFileSync(hookConfigPath, "utf8").includes("must-not-enter-hook-config"));

  // Security regression: per-run artifacts are private and executable only where needed.
  assert.equal(statSync(stagedRoot).mode & 0o777, 0o700);
  assert.equal(statSync(hooksDirectory).mode & 0o777, 0o700);
  assert.equal(statSync(managerEnv.CONTAINERS_CONF_OVERRIDE).mode & 0o777, 0o600);
  assert.equal(statSync(hookConfigPath).mode & 0o777, 0o600);
  assert.equal(statSync(hookScript).mode & 0o777, 0o700);
  assert.equal(statSync(hookTemplate).mode & 0o777, 0o600);

  // Negative control: an inherited override is rejected instead of silently replacing it.
  assert.throws(
    () => stageLinuxDevelopmentNacHook(repositoryRoot, { CONTAINERS_CONF_OVERRIDE: "/tmp/operator.conf" }),
    /CONTAINERS_CONF_OVERRIDE is already set.*unset it and retry/,
  );
  const incompleteRepository = mkdtempSync(path.join(os.tmpdir(), "kautuka-missing-nac-payload-"));
  try {
    assert.throws(
      () => stageLinuxDevelopmentNacHook(incompleteRepository, {}),
      /Linux development NAC hook payload is incomplete/,
    );
  } finally {
    rmSync(incompleteRepository, { recursive: true, force: true });
  }
  process.stdout.write("Linux launcher NAC hook checks passed (fail-first staging and override negative control).\n");
} finally {
  removeLinuxDevelopmentNacHook(staged?.directory);
}

// The fallback matches Manager's Linux home-state path when XDG_STATE_HOME is absent,
// and a changed HOME is reflected in both Manager's effective XDG value and hook env.
const alteredHome = path.join(os.tmpdir(), "kautuka-home-with-spaces");
const noXdgStage = stageLinuxDevelopmentNacHook(repositoryRoot, { HOME: alteredHome });
try {
  assert.equal(noXdgStage.managerEnvironment.XDG_STATE_HOME, path.join(alteredHome, ".local", "state"));
  const configName = readdirSync(path.join(noXdgStage.directory, "hooks.d")).find((name) => name.endsWith(".json"));
  const config = JSON.parse(readFileSync(path.join(noXdgStage.directory, "hooks.d", configName), "utf8"));
  const env = Object.fromEntries(config.hook.env.map((entry) => {
    const separator = entry.indexOf("=");
    return [entry.slice(0, separator), entry.slice(separator + 1)];
  }));
  assert.equal(env.HOME, alteredHome);
  assert.equal(env.XDG_STATE_HOME, noXdgStage.managerEnvironment.XDG_STATE_HOME);
} finally {
  removeLinuxDevelopmentNacHook(noXdgStage.directory);
}

  // Exercise a real detached process group with a child descendant. Shutdown must
  // wait until SIGTERM removes the entire group before allowing stage cleanup.
  const parentSource = [
    "const { spawn } = require('node:child_process');",
    "spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });",
    "setInterval(() => {}, 1000);",
  ].join(" ");
  const groupProcess = spawn(process.execPath, ["-e", parentSource], {
    detached: true,
    stdio: "ignore",
  });
  await new Promise((resolve, reject) => {
    groupProcess.once("spawn", resolve);
    groupProcess.once("error", reject);
  });
  try {
    assert.equal(await killProcessTree(groupProcess), true, "real process group and descendant stop before cleanup");
    assert.equal(canRemoveLinuxDevelopmentNacHook([true]), true);
  } finally {
    try {
      process.kill(-groupProcess.pid, "SIGKILL");
    } catch {
      // The group has already been reaped.
    }
  }

  // Retention negative control: a simulated group that survives SIGKILL must make
  // the shutdown gate refuse to remove files that the group could still use.
  const stubbornProcessApi = { kill(_pid, _signal) {} };
  const stubbornGroupStopped = await killProcessTree(
    { pid: 987654 },
    { platform: "linux", processApi: stubbornProcessApi, termTimeoutMs: 10, killTimeoutMs: 10, pollIntervalMs: 2 },
  );
  assert.equal(stubbornGroupStopped, false);
  assert.equal(canRemoveLinuxDevelopmentNacHook([true, stubbornGroupStopped]), false);
  process.stdout.write("Linux process-group termination and retention checks passed.\n");

} else {
  process.stdout.write("Linux launcher NAC hook checks skipped outside Linux.\n");
}
