import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { spawn } from "node:child_process";
import { createServer } from "node:net";
import { lstatSync, mkdtempSync, rmSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { once } from "node:events";

import { isUnixSocketAcceptingConnections } from "./launcher-readiness.mjs";

const temporaryDirectories = [];
const servers = [];
const children = [];

afterEach(async () => {
  const activeChildren = children.splice(0).filter((child) =>
    child.pid !== undefined && child.exitCode === null && child.signalCode === null);
  const exits = activeChildren.map((child) => once(child, "exit"));
  for (const child of activeChildren) child.kill("SIGKILL");
  await Promise.all(exits);
  await Promise.all(servers.splice(0).map((server) => new Promise((resolve) => server.close(resolve))));
  for (const directory of temporaryDirectories.splice(0)) rmSync(directory, { recursive: true, force: true });
});

test("a stale Unix socket path is not ready", async () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), "kautuka-launch-readiness-"));
  temporaryDirectories.push(directory);
  const socketPath = path.join(directory, "manager.sock");
  await import("node:fs/promises").then(({ writeFile }) => writeFile(socketPath, "stale"));

  assert.equal(await isUnixSocketAcceptingConnections(socketPath, 100), false);
});

test("a stale Unix socket inode left by a killed listener is not ready", { skip: process.platform !== "linux" }, async () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), "kautuka-launch-readiness-"));
  temporaryDirectories.push(directory);
  const socketPath = path.join(directory, "manager.sock");
  const child = spawn(process.execPath, [
    "-e",
    "const server = require('node:net').createServer(); server.listen(process.argv[1], () => process.stdout.write('ready\\n'));",
    socketPath,
  ], { stdio: ["ignore", "pipe", "pipe"] });
  children.push(child);

  let stderr = "";
  child.stderr.setEncoding("utf8").on("data", (chunk) => { stderr += chunk; });
  await new Promise((resolve, reject) => {
    let output = "";
    child.stdout.setEncoding("utf8").on("data", (chunk) => {
      output += chunk;
      if (output.includes("ready\n")) resolve();
    });
    child.once("error", reject);
    child.once("exit", (code, signal) => reject(new Error(`socket fixture exited before binding (code=${code}, signal=${signal}): ${stderr}`)));
  });

  child.kill("SIGKILL");
  await once(child, "exit");
  assert.equal(lstatSync(socketPath).isSocket(), true, "the killed listener must leave a socket inode behind");
  assert.equal(await isUnixSocketAcceptingConnections(socketPath, 100), false);
});

test("a listening Unix socket is ready", { skip: process.platform === "win32" }, async () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), "kautuka-launch-readiness-"));
  temporaryDirectories.push(directory);
  const socketPath = path.join(directory, "manager.sock");
  const server = createServer();
  servers.push(server);
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(socketPath, resolve);
  });

  assert.equal(await isUnixSocketAcceptingConnections(socketPath, 100), true);
});
