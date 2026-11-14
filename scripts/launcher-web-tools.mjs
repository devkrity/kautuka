import path from "node:path";
import { spawnSync } from "node:child_process";

function windowsPowerShell() {
  if (spawnSync("pwsh", ["-NoProfile", "-Command", "$PSVersionTable.PSVersion.Major"], { stdio: "ignore" }).status === 0) return "pwsh";
  return "powershell.exe";
}

/** Return the repository's native web-tools MCPB build command for development. */
export function webToolsMcpbCommand(platform, repoRoot) {
  const script = platform === "win32"
    ? path.join(repoRoot, "mcps", "kautuka-web-tools", "build-mcpb.ps1")
    : platform === "linux" || platform === "darwin"
      ? path.join(repoRoot, "mcps", "kautuka-web-tools", "build-mcpb.sh")
      : null;
  if (!script) throw new Error(`Development web-tools MCPB staging is unsupported on ${platform}.`);
  return platform === "win32"
    ? { command: windowsPowerShell(), args: ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script] }
    : { command: "bash", args: [script] };
}
