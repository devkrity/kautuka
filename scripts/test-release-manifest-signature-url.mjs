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
const windowsInstaller = await fs.readFile(
  path.join(repoRoot, "src", "installer", "Kautuka.Installer.Windows", "Installation", "WindowsApplianceInstaller.cs"),
  "utf8",
);
const windowsRelease = await fs.readFile(
  path.join(repoRoot, "src", "installer", "Kautuka.Installer.Windows", "Installation", "WindowsApplianceInstaller.Release.cs"),
  "utf8",
);
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

  const generateRaw = (extraArgs = []) => spawnSync(process.execPath, [
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
  const generate = (extraArgs = []) => {
    const result = generateRaw(extraArgs);
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
  assert.equal(manifest.signature.path, "kautuka-platform-release.sig", "signature asset has the published flat filename");
  assert.equal(Object.hasOwn(manifest, "applianceDeployment"), false, "legacy releases do not opt into an appliance deployment");
  assert.equal(cryptoVerify("sha256", manifestBytes, publicKey, signature), true, "default release manifest signature remains valid");

  const signatureAssetName = windowsInstaller.match(/releaseManifestSignatureAssetName = "([^"]+)"/u)?.[1];
  assert.equal(signatureAssetName, manifest.signature.path, "Windows installer trusts the manifest's actual signature asset name");
  assert.match(
    windowsRelease,
    /expectedSignatureUrl = \$"\{releaseBase\}\/\{releaseManifestSignatureAssetName\}"/u,
    "Windows installer binds the signature URL to the exact release tag and the actual .sig asset name",
  );
  assert.match(
    windowsRelease,
    /manifest\.Signature\?\.Path, releaseManifestSignatureAssetName/u,
    "Windows installer validates the manifest signature path against the actual .sig asset name",
  );
  assert.doesNotMatch(
    windowsRelease,
    /releaseManifestAssetName\}\.sig/u,
    "Windows installer must not expect the nonexistent .json.sig asset",
  );
  const exactTagSignatureUrl = `https://github.com/devkrity/kautuka/releases/download/v0.1.20-preview.1/${signatureAssetName}`;
  const wrongSignatureUrl = `https://github.com/devkrity/kautuka/releases/download/v0.1.20-preview.1/${manifest.signature.path}.json.sig`;
  assert.notEqual(wrongSignatureUrl, exactTagSignatureUrl, "a .json.sig URL is rejected as a different release asset");

  const exactReleaseBase = "https://github.com/devkrity/kautuka/releases/download/v0.1.20-preview.1";
  generate(["--artifact-base-url", exactReleaseBase]);
  manifestBytes = await fs.readFile(path.join(outputDir, "kautuka-platform-release.json"));
  manifest = JSON.parse(manifestBytes.toString("utf8"));
  signature = Buffer.from((await fs.readFile(path.join(outputDir, "kautuka-platform-release.sig"), "utf8")).trim(), "base64");
  assert.equal(manifest.signature.path, "kautuka-platform-release.sig");
  assert.equal(manifest.signature.url, `${exactReleaseBase}/kautuka-platform-release.sig`);
  assert.equal(manifest.artifacts.find((asset) => asset.platform === "wsl-amd64").url, `${exactReleaseBase}/kautuka-wsl-appliance.tar.gz`);
  assert.equal(manifest.artifacts.find((asset) => asset.platform === "linux-x64").url, `${exactReleaseBase}/kautuka-linux-amd64.tar.gz`);
  assert.equal(cryptoVerify("sha256", manifestBytes, publicKey, signature), true, "exact-tag artifact and signature URLs remain covered by the manifest signature");

  for (const [type, options] of [["rsa", { modulusLength: 2048 }], ["ec", { namedCurve: "secp384r1" }]]) {
    const badKey = generateKeyPairSync(type, options).privateKey;
    const badKeyPath = path.join(tempRoot, `${type}-wrong-key.pem`);
    await fs.writeFile(badKeyPath, badKey.export({ type: "pkcs8", format: "pem" }));
    const rejected = generateRaw(["--private-key", badKeyPath]);
    assert.notEqual(rejected.status, 0, `${type} key must be rejected`);
    assert.match(rejected.stderr, /ECDSA P-256/);
    assert.deepEqual(await fs.readFile(path.join(outputDir, "kautuka-platform-release.json")), manifestBytes,
      "rejecting a key must not overwrite the existing manifest");
  }

  const deploymentPath = path.join(tempRoot, "appliance-deployment.json");
  const deployment = {
    schemaVersion: 1,
    hookAbi: 1,
    services: Object.fromEntries(["credentialshost", "channelhost", "gateway", "webhost", "updateservice"]
      .map((role) => [role, { image: `ghcr.io/devkrity/kautuka-${role}@sha256:${"a".repeat(64)}` }])),
  };
  deployment.services.gateway.image = `registry.example:5000/platform/core__gateway--service@sha256:${"b".repeat(64)}`;
  await fs.writeFile(deploymentPath, JSON.stringify(deployment));
  generate(["--appliance-deployment", deploymentPath]);
  const applianceBytes = await fs.readFile(path.join(outputDir, "kautuka-platform-release.json"));
  const applianceSignature = Buffer.from((await fs.readFile(path.join(outputDir, "kautuka-platform-release.sig"), "utf8")).trim(), "base64");
  const applianceManifest = JSON.parse(applianceBytes);
  assert.deepEqual(applianceManifest.applianceDeployment, deployment);
  assert.equal(cryptoVerify("sha256", applianceBytes, publicKey, applianceSignature), true);
  const changedBytes = Buffer.from(applianceBytes.toString("utf8").replace("a".repeat(64), "b".repeat(64)), "utf8");
  assert.equal(cryptoVerify("sha256", changedBytes, publicKey, applianceSignature), false, "changing a service image invalidates the signature");

  const missingRole = structuredClone(deployment);
  delete missingRole.services.updateservice;
  const mutableImage = structuredClone(deployment);
  mutableImage.services.gateway.image = "ghcr.io/devkrity/kautuka-gateway:latest";
  const shortImage = structuredClone(deployment);
  shortImage.services.gateway.image = `devkrity/gateway@sha256:${"a".repeat(64)}`;
  const badRegistryPort = structuredClone(deployment);
  badRegistryPort.services.gateway.image = deployment.services.gateway.image.replace(":5000/", ":65536/");
  const arbitraryRunOption = structuredClone(deployment);
  arbitraryRunOption.services.gateway.privileged = true;
  for (const invalid of [missingRole, mutableImage, shortImage, badRegistryPort, arbitraryRunOption, { ...deployment, hookAbi: 0 }]) {
    await fs.writeFile(deploymentPath, JSON.stringify(invalid));
    const result = generateRaw(["--appliance-deployment", deploymentPath]);
    assert.notEqual(result.status, 0, "invalid appliance layout must not be signed");
    assert.deepEqual(await fs.readFile(path.join(outputDir, "kautuka-platform-release.json")), applianceBytes);
  }

  const platformPayload = path.join(tempRoot, "linux-payload");
  await fs.mkdir(path.join(platformPayload, "service-images"), { recursive: true });
  const tar = spawnSync("tar", ["-czf", path.join(outputDir, "kautuka-linux-amd64.tar.gz"),
    "-C", platformPayload, "service-images"], { encoding: "utf8" });
  assert.equal(tar.status, 0, tar.stderr || tar.stdout);
  await fs.writeFile(deploymentPath, JSON.stringify(deployment));
  generate(["--appliance-deployment", deploymentPath]);
  const publicKeyPath = path.join(tempRoot, "signing-public.pem");
  await fs.writeFile(publicKeyPath, publicKeyPem);
  const verification = spawnSync(process.execPath, [
    path.join(repoRoot, "scripts", "verify-release-artifacts.mjs"),
    "--directory", outputDir,
    "--version", "0.1.20-preview.1",
    "--channel", "preview",
    "--source-sha", "7ad4cf15dd8df78e9f97ebe25ad0ba15ce2c1731",
    "--public-key", publicKeyPath,
  ], { encoding: "utf8" });
  assert.notEqual(verification.status, 0, "a valid signature is insufficient when the actual appliance image is missing");
  assert.match(verification.stderr, /appliance image archive is missing or not a regular file: credentialshost/);

  assert.match(buildAssets, /--signature-url\)/, "release builder parses the optional exact signature URL");
  assert.match(buildAssets, /manifest_args\+\=\(--signature-url "\$signature_url"\)/, "release builder passes the explicit URL into the signed manifest generator");
  assert.match(localRelease, /--signature-url "\$public_base_url\/manifest\.sig"/, "local release feed uses the installer-derived signature route");

  console.log("Release manifest signature URL override is signed, defaults remain unchanged, and local feed wiring matches installer signature derivation.");
} finally {
  await fs.rm(tempRoot, { recursive: true, force: true });
}
