#!/usr/bin/env node
/**
 * Launch the local Kautuka native service stack (Windows, macOS, Linux).
 * Replaces the former VS Code "Debug Native Stack" compound launch config.
 *
 * Usage:
 *   node scripts/launch-kautuka.mjs            # build + start all services
 *   node scripts/launch-kautuka.mjs --skip-build
 */

import { spawn } from "node:child_process";
import { appendFileSync, existsSync, mkdirSync, readdirSync } from "node:fs";
import { createConnection } from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, "..");
const telegramDiagnosticsPath = process.argv.includes("--telegram-diagnostics")
  ? path.join(repoRoot, ".artifacts", `telegram-callback-${Date.now()}.jsonl`)
  : null;
let diagnosticLines = 0;

function captureTelegramDiagnostic(prefix, line) {
  const message = line.trim();
  if (!telegramDiagnosticsPath || diagnosticLines >= 10000 || prefix !== "ChannelHost"
    || !(message.startsWith("Telegram poll ") || message.startsWith("Telegram callback "))) return;
  try {
    mkdirSync(path.dirname(telegramDiagnosticsPath), { recursive: true });
    appendFileSync(telegramDiagnosticsPath, JSON.stringify({ observedAtUtc: new Date().toISOString(), message }) + "\n");
    diagnosticLines++;
  } catch (error) {
    diagnosticLines = 10000;
    process.stderr.write(`[launch-kautuka] Telegram diagnostic capture stopped: ${error.code ?? "write_failed"}\n`);
  }
}

// Everything except WebHost talks over a Unix socket, so readiness is the appearance of
// the socket file rather than an open port. Keep these names in step with the service
// names in KautukaHostNetworkPolicy, which decide the socket filenames.
const socketRoot = path.join(os.homedir(), ".kautuka", "runtime", "sockets");

const SERVICES = [
  {
    name: "CredentialsHost",
    project: "src/backend/Kautuka.CredentialsHost/Kautuka.CredentialsHost.csproj",
    readySocket: "credentialshost",
  },
  {
    name: "ChannelHost",
    project: "src/backend/Kautuka.ChannelHost/Kautuka.ChannelHost.csproj",
    readySocket: "channelhost",
  },
  {
    name: "Manager",
    project: "src/backend/Kautuka.Manager/Kautuka.Manager.csproj",
    readySocket: "manager",
  },
  {
    name: "UpdateService",
    project: "src/backend/Kautuka.UpdateService/Kautuka.UpdateService.csproj",
    readySocket: "updateservice",
  },
  {
    name: "Gateway",
    project: "src/backend/Kautuka.Gateway/Kautuka.Gateway.csproj",
    readySocket: "gateway",
  },
  {
    name: "WebHost",
    project: "src/backend/Kautuka.WebHost/Kautuka.WebHost.csproj",
    readyPort: 20229,
  },
];

function socketPathFor(service) {
  return path.join(socketRoot, service.readySocket, `${service.readySocket}.sock`);
}

/**
 * A live socket file cannot be stat'd on Windows - it is a reparse point and stat fails
 * with EACCES, which makes fs.existsSync report false for a socket that is serving.
 * Reading the directory does not stat its entries, so it sees the socket on every platform.
 */
function socketFileExists(socketPath) {
  try {
    return readdirSync(path.dirname(socketPath)).includes(path.basename(socketPath));
  } catch {
    return false;
  }
}

/** @type {{ name: string, process: import('node:child_process').ChildProcess }[]} */
const children = [];
let shuttingDown = false;

function runCommand(label, command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: options.cwd ?? repoRoot,
      stdio: "inherit",
      env: process.env,
    });

    child.on("error", reject);
    child.on("exit", (code, signal) => {
      if (code === 0) {
        resolve(undefined);
        return;
      }

      reject(
        new Error(
          `${label} failed (exit ${code ?? "null"}, signal ${signal ?? "null"})`,
        ),
      );
    });
  });
}

function prefixStream(stream, prefix, target) {
  let buffer = "";

  stream.on("data", (chunk) => {
    buffer += chunk.toString();
    const lines = buffer.split(/\r?\n/);
    buffer = lines.pop() ?? "";

    for (const line of lines) {
      if (line.length > 0) {
        captureTelegramDiagnostic(prefix, line);
        target.write(`[${prefix}] ${line}\n`);
      }
    }
  });

  stream.on("end", () => {
    if (buffer.length > 0) {
      captureTelegramDiagnostic(prefix, buffer);
      target.write(`[${prefix}] ${buffer}\n`);
    }
  });
}

function spawnService(service) {
  const projectPath = path.join(repoRoot, service.project);
  if (!existsSync(projectPath)) {
    throw new Error(`Project not found: ${projectPath}`);
  }

  const child = spawn(
    "dotnet",
    ["run", "--no-build", "--project", projectPath],
    {
      cwd: repoRoot,
      stdio: ["ignore", "pipe", "pipe"],
      env: process.env,
      ...(process.platform !== "win32" ? { detached: true } : {}),
    },
  );

  if (!child.stdout || !child.stderr) {
    throw new Error(`Failed to capture output for ${service.name}`);
  }

  prefixStream(child.stdout, service.name, process.stdout);
  prefixStream(child.stderr, service.name, process.stderr);

  child.on("exit", (code, signal) => {
    if (shuttingDown) {
      return;
    }

    process.stderr.write(
      `[launch-kautuka] ${service.name} exited (code=${code ?? "null"}, signal=${signal ?? "null"})\n`,
    );
    void shutdown(typeof code === "number" && code !== 0 ? code : 1);
  });

  children.push({ name: service.name, process: child });
}

// Gateway performs a bounded built-in MCP metadata bootstrap before binding.  Give
// every service the same two-minute startup ceiling so a legitimate bootstrap
// cannot race the launcher readiness check.
function waitForPort(service, timeoutMs = 120_000) {
  const deadline = Date.now() + timeoutMs;

  return new Promise((resolve, reject) => {
    const probe = () => {
      if (service.process.exitCode !== null) {
        reject(new Error(`${service.name} exited before becoming ready`));
        return;
      }

      if (service.readySocket) {
        // Kestrel creates the socket file as it binds. Node cannot dial an AF_UNIX
        // socket on Windows, where a path is treated as a named pipe, so the file
        // appearing is the portable readiness signal.
        const socketPath = socketPathFor(service);
        if (socketFileExists(socketPath)) {
          resolve(undefined);
          return;
        }

        if (Date.now() >= deadline) {
          reject(new Error(`${service.name} did not create ${socketPath} within ${timeoutMs}ms`));
          return;
        }

        setTimeout(probe, 200);
        return;
      }

      const socket = createConnection({ host: "127.0.0.1", port: service.readyPort });
      socket.setTimeout(1_000);
      socket.once("connect", () => {
        socket.destroy();
        resolve(undefined);
      });
      const retry = () => {
        socket.destroy();
        if (Date.now() >= deadline) {
          reject(new Error(`${service.name} did not listen on port ${service.readyPort} within ${timeoutMs}ms`));
          return;
        }
        setTimeout(probe, 200);
      };
      socket.once("error", retry);
      socket.once("timeout", retry);
    };

    probe();
  });
}

function killProcessTree(child) {
  return new Promise((resolve) => {
    if (!child.pid) {
      resolve(undefined);
      return;
    }

    if (process.platform === "win32") {
      const killer = spawn(
        "taskkill",
        ["/pid", String(child.pid), "/T", "/F"],
        { stdio: "ignore", windowsHide: true },
      );
      killer.on("exit", () => resolve(undefined));
      return;
    }

    try {
      process.kill(-child.pid, "SIGTERM");
    } catch {
      resolve(undefined);
      return;
    }

    setTimeout(() => {
      try {
        process.kill(-child.pid, "SIGKILL");
      } catch {
        // already stopped
      }
      resolve(undefined);
    }, 2000);
  });
}

async function shutdown(exitCode = 0) {
  if (shuttingDown) {
    return;
  }

  shuttingDown = true;
  process.stderr.write("[launch-kautuka] shutting down services...\n");

  await Promise.all(
    children.map(async ({ name, process: child }) => {
      try {
        await killProcessTree(child);
      } catch (error) {
        process.stderr.write(
          `[launch-kautuka] failed to stop ${name}: ${error instanceof Error ? error.message : String(error)}\n`,
        );
      }
    }),
  );

  process.exit(exitCode);
}

async function main() {
  if (telegramDiagnosticsPath) {
    process.stdout.write(`[launch-kautuka] Telegram timing capture: ${telegramDiagnosticsPath} (up to 10000 records)\n`);
  }
  const skipBuild = process.argv.includes("--skip-build");

  if (!skipBuild) {
    process.stdout.write("[launch-kautuka] building solution...\n");
    await runCommand("dotnet build", "dotnet", [
      "build",
      "src/Kautuka.slnx",
      "-c",
      "Debug",
    ]);

    const frontendDir = path.join(repoRoot, "src/frontend/web");
    process.stdout.write(
      "[launch-kautuka] building web assets for WebHost...\n",
    );
    await runCommand(
      "frontend build",
      "node",
      ["./node_modules/vite/bin/vite.js", "build"],
      { cwd: frontendDir },
    );
  }

  process.stdout.write("[launch-kautuka] staging Microsoft 365 mail connector...\n");
  await runCommand("stage m365 mail", "node", ["scripts/stage-m365-mail-mcpb.mjs"]);
  process.stdout.write("[launch-kautuka] staging Google mail connector...\n");
  await runCommand("stage google mail", "node", ["scripts/stage-google-mail-mcpb.mjs"]);

  process.stdout.write("[launch-kautuka] starting services...\n");
  for (const service of SERVICES) {
    spawnService(service);
    const child = children.at(-1);
    await waitForPort({ ...service, process: child.process });
    const readyOn = service.readySocket ? socketPathFor(service) : `port ${service.readyPort}`;
    process.stdout.write(`[launch-kautuka] ${service.name} ready on ${readyOn}.\n`);
  }

  process.stdout.write(
    "[launch-kautuka] stack running. UI (WebHost): http://localhost:20229 | "
      + "Gateway API: via WebHost /api (Gateway itself listens on a Unix socket) | Ctrl+C to stop.\n",
  );
}

process.on("SIGINT", () => {
  void shutdown(0);
});
process.on("SIGTERM", () => {
  void shutdown(0);
});

main().catch((error) => {
  process.stderr.write(
    `[launch-kautuka] ${error instanceof Error ? error.message : String(error)}\n`,
  );
  void shutdown(1);
});
