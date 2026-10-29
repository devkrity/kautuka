#!/usr/bin/env node

import assert from "node:assert/strict";
import { generateKeyPairSync, verify as cryptoVerify } from "node:crypto";
import { promises as fs } from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const generator = path.join(repoRoot, "scripts", "generate-release-manifest.mjs");
const buildAssets = await fs.readFile(path.join(repoRoot, "scripts", "build-release-assets.sh"), "utf8");
const localRelease = await fs.readFile(path.join(repoRoot, "scripts", "local-release.sh"), "utf8");
const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "kautuka-release-signature-url-"));

try {
  const outputDir = path.join(tempRoot, "release");
  await fs.mkdir(outputDir);
  await fs.writeFile(path.join(outputDir, "kautuka-linux-amd64.tar.gz"), "linux test artifact\n");
  await fs.writeFile(path.join(outputDir, "kautuka-wsl-appliance.tar.gz"), "wsl test artifact\n");

  const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  const privateKeyPath = path.join(tempRoot, "signing-private.pem");
  await fs.writeFile(privateKeyPath, privateKey.export({ type: "pkcs8", format: "pem" }));
  const publicKeyPem = publicKey.export({ type: "spki", format: "pem" });
  const signatureUrl = "http://127.0.0.1:20300/manifest.sig";

  const generate = (extraArgs = []) => {
    const result = spawnSync(process.execPath, [
      generator,
      "--output-dir", outputDir,
      "--private-key", privateKeyPath,
      "--release-id", "v0.1.20-preview.1",
      "--release-tag", "v0.1.20-preview.1",
      "--version", "0.1.20-preview.1",
      "--source-sha", "7ad4cf15dd8df78e9f97ebe25ad0ba15ce2c1731",
      "--channel", "preview",
      "--artifact-base-url", "http://127.0.0.1:20300",
      "--published-at", "2026-10-29T00:00:00.000Z",
      ...extraArgs,
    ], { encoding: "utf8" });
    assert.equal(result.status, 0, result.stderr || result.stdout);
  };

  generate(["--signature-url", signatureUrl]);
  let manifestBytes = await fs.readFile(path.join(outputDir, "kautuka-platform-release.json"));
  let manifest = JSON.parse(manifestBytes.toString("utf8"));
  let signature = Buffer.from((await fs.readFile(path.join(outputDir, "kautuka-platform-release.sig"), "utf8")).trim(), "base64");
  assert.equal(manifest.signature.url, signatureUrl);
  assert.equal(cryptoVerify("sha256", manifestBytes, publicKey, signature), true, "overridden URL remains covered by the release signature");

  generate();
  manifestBytes = await fs.readFile(path.join(outputDir, "kautuka-platform-release.json"));
  manifest = JSON.parse(manifestBytes.toString("utf8"));
  signature = Buffer.from((await fs.readFile(path.join(outputDir, "kautuka-platform-release.sig"), "utf8")).trim(), "base64");
  assert.equal(manifest.signature.url, "http://127.0.0.1:20300/kautuka-platform-release.sig", "default remains the release-asset URL");
  assert.equal(cryptoVerify("sha256", manifestBytes, publicKey, signature), true, "default release manifest signature remains valid");

  assert.match(buildAssets, /--signature-url\)/, "release builder parses the optional exact signature URL");
  assert.match(buildAssets, /manifest_args\+\=\(--signature-url "\$signature_url"\)/, "release builder passes the explicit URL into the signed manifest generator");
  assert.match(localRelease, /--signature-url "\$public_base_url\/manifest\.sig"/, "local release feed uses the installer-derived signature route");

  console.log("Release manifest signature URL override is signed, defaults remain unchanged, and local feed wiring matches installer signature derivation.");
} finally {
  await fs.rm(tempRoot, { recursive: true, force: true });
}
