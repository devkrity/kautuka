#!/usr/bin/env node
/**
 * Build the Google mail connector bundle for local dev.
 * Gateway imports artifacts/mcpb/kautuka-google-mail-*.mcpb on startup, the same
 * way it imports kautuka-web-tools. The program inside the bundle is linux-x64
 * because the local sandbox image is Linux.
 *
 * Usage: node scripts/stage-google-mail-mcpb.mjs
 */
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, copyFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, "..");
const connectorDir = path.join(repoRoot, "src", "backend", "connectors", "google-mail");
const projectPath = path.join(connectorDir, "kautuka-google-mail.csproj");
const manifestPath = path.join(connectorDir, "manifest.json");
const publishDir = path.join(repoRoot, "artifacts", "google-mail-linux-x64");
const packDir = path.join(repoRoot, "artifacts", "google-mail-pack");

async function runNpx(label, args) {
  if (process.platform === "win32") {
    const npxCli = path.join(path.dirname(process.execPath), "node_modules", "npm", "bin", "npx-cli.js");
    await run(label, process.execPath, [npxCli, ...args]);
    return;
  }

  await run(label, "npx", args);
}

function run(label, command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: options.cwd ?? repoRoot,
      stdio: "inherit",
      shell: options.shell ?? false,
      env: process.env,
    });
    child.on("error", reject);
    child.on("exit", (code, signal) => {
      if (code === 0) {
        resolve(undefined);
        return;
      }
      reject(new Error(`${label} failed (exit ${code ?? "null"}, signal ${signal ?? "null"})`));
    });
  });
}

function newestInputMs() {
  let newest = statSync(manifestPath).mtimeMs;
  const stack = [connectorDir];
  while (stack.length > 0) {
    const current = stack.pop();
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      if (entry.name === "bin" || entry.name === "obj" || entry.name === "client-id.txt" || entry.name === "client-secret.txt") {
        continue;
      }
      const fullPath = path.join(current, entry.name);
      if (entry.isDirectory()) {
        stack.push(fullPath);
        continue;
      }
      const modified = statSync(fullPath).mtimeMs;
      if (modified > newest) {
        newest = modified;
      }
    }
  }
  return newest;
}

function assertElf(binaryPath) {
  const header = readFileSync(binaryPath).subarray(0, 4);
  const elf = header.length === 4 && header[0] === 0x7f && header[1] === 0x45 && header[2] === 0x4c && header[3] === 0x46;
  if (!elf) {
    throw new Error(`${binaryPath} is not a Linux executable. The sandbox image cannot run it.`);
  }
}

const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
const version = manifest.version;
if (typeof version !== "string" || version.length === 0) {
  throw new Error("google-mail manifest.json has no version.");
}

const outputFile = path.join(repoRoot, "artifacts", "mcpb", `kautuka-google-mail-${version}.mcpb`);
if (existsSync(outputFile) && statSync(outputFile).mtimeMs >= newestInputMs()) {
  process.stdout.write(`[stage-google-mail] up to date: ${outputFile}\n`);
  process.exit(0);
}

process.stdout.write("[stage-google-mail] publishing linux-x64 connector...\n");
await run("dotnet publish", "dotnet", [
  "publish",
  projectPath,
  "-c",
  "Debug",
  "-r",
  "linux-x64",
  "--self-contained",
  "-p:PublishAot=false",
  "-o",
  publishDir,
]);

const binaryPath = path.join(publishDir, "kautuka-google-mail");
if (!existsSync(binaryPath)) {
  throw new Error(`Publish did not produce ${binaryPath}.`);
}
assertElf(binaryPath);

rmSync(packDir, { recursive: true, force: true });
mkdirSync(packDir, { recursive: true });
copyFileSync(manifestPath, path.join(packDir, "manifest.json"));
for (const entry of readdirSync(publishDir, { withFileTypes: true })) {
  if (!entry.isFile() || entry.name.endsWith(".pdb")) {
    continue;
  }
  copyFileSync(path.join(publishDir, entry.name), path.join(packDir, entry.name));
}

mkdirSync(path.dirname(outputFile), { recursive: true });
rmSync(outputFile, { force: true });
process.stdout.write("[stage-google-mail] packing mcpb...\n");
await runNpx("mcpb validate", ["-y", "@anthropic-ai/mcpb", "validate", packDir]);
await runNpx("mcpb pack", ["-y", "@anthropic-ai/mcpb", "pack", packDir, outputFile]);

if (!existsSync(outputFile)) {
  throw new Error(`Failed to create ${outputFile}.`);
}

process.stdout.write(`[stage-google-mail] wrote ${outputFile}\n`);
