import { spawn } from "node:child_process";

/** Stop a launched service's process group and report whether it is gone. */
export function killProcessTree(child, options = {}) {
  const platform = options.platform ?? process.platform;
  const spawnProcess = options.spawnProcess ?? spawn;
  const processApi = options.processApi ?? process;
  if (!child.pid) return Promise.resolve(true);

  if (platform === "win32") {
    return new Promise((resolve) => {
      const killer = spawnProcess(
        "taskkill",
        ["/pid", String(child.pid), "/T", "/F"],
        { stdio: "ignore", windowsHide: true },
      );
      killer.once("error", () => resolve(false));
      killer.once("exit", (code) => resolve(code === 0 || child.exitCode != null || child.signalCode != null));
    });
  }

  const groupExists = () => {
    try {
      processApi.kill(-child.pid, 0);
      return true;
    } catch (error) {
      return error?.code !== "ESRCH";
    }
  };
  const pollIntervalMs = options.pollIntervalMs ?? 100;
  const waitForGroupExit = async (timeoutMs) => {
    const deadline = Date.now() + timeoutMs;
    while (groupExists() && Date.now() < deadline) {
      await new Promise((done) => setTimeout(done, pollIntervalMs));
    }
    return !groupExists();
  };

  return (async () => {
    try {
      processApi.kill(-child.pid, "SIGTERM");
    } catch {
      // The group may already have exited.
    }
    if (await waitForGroupExit(options.termTimeoutMs ?? 2000)) return true;
    try {
      processApi.kill(-child.pid, "SIGKILL");
    } catch {
      // The group may have exited between the probe and signal.
    }
    return waitForGroupExit(options.killTimeoutMs ?? 5000);
  })();
}
