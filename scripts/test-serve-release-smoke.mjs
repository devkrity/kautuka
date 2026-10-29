#!/usr/bin/env node

import assert from "node:assert/strict";
import { once } from "node:events";
import { spawn } from "node:child_process";
import { promises as fs } from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const serverScript = path.join(scriptDir, "serve-release-smoke.mjs");
const fixtureRoot = await fs.mkdtemp(path.join(os.tmpdir(), "kautuka-feed-alias-"));
let child;

async function reserveLoopbackPort() {
  const reservation = net.createServer();
  reservation.listen(0, "127.0.0.1");
  await once(reservation, "listening");
  const { port } = reservation.address();
  await new Promise((resolve, reject) => reservation.close((error) => error ? reject(error) : resolve()));
  return port;
}

try {
  const manifest = Buffer.from('{"version":"0.1.20-preview.1"}\n');
  const signature = Buffer.from("test-signature-bytes\n");
  await fs.writeFile(path.join(fixtureRoot, "kautuka-platform-release.json"), manifest);
  await fs.writeFile(path.join(fixtureRoot, "kautuka-platform-release.sig"), signature);

  const port = await reserveLoopbackPort();
  child = spawn(process.execPath, [serverScript, "--dir", fixtureRoot, "--host", "127.0.0.1", "--port", String(port)], {
    stdio: ["ignore", "ignore", "pipe"],
  });
  let stderr = "";
  child.stderr.setEncoding("utf8");
  child.stderr.on("data", (chunk) => { stderr += chunk; });

  const baseUrl = `http://127.0.0.1:${port}`;
  let manifestResponse;
  for (let attempt = 0; attempt < 50; attempt += 1) {
    if (child.exitCode !== null) throw new Error(`local feed server exited early: ${stderr}`);
    try {
      manifestResponse = await fetch(`${baseUrl}/manifest`, { signal: AbortSignal.timeout(300) });
      break;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
  }

  assert.ok(manifestResponse, "local feed server did not start on its loopback test port");
  assert.equal(manifestResponse.status, 200);
  assert.deepEqual(Buffer.from(await manifestResponse.arrayBuffer()), manifest);

  const signatureResponse = await fetch(`${baseUrl}/manifest.sig`, { signal: AbortSignal.timeout(1000) });
  assert.equal(signatureResponse.status, 200, "installer-derived signature alias must resolve");
  assert.deepEqual(Buffer.from(await signatureResponse.arrayBuffer()), signature);

  const directSignatureResponse = await fetch(`${baseUrl}/kautuka-platform-release.sig`, { signal: AbortSignal.timeout(1000) });
  assert.equal(directSignatureResponse.status, 200);
  assert.deepEqual(Buffer.from(await directSignatureResponse.arrayBuffer()), signature);

  console.log("Local release feed serves the signed-manifest and installer-derived signature aliases over loopback.");
} finally {
  if (child && child.exitCode === null) {
    child.kill("SIGTERM");
    await once(child, "exit");
  }
  await fs.rm(fixtureRoot, { recursive: true, force: true });
}
