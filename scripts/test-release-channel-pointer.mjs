#!/usr/bin/env node

import assert from "node:assert/strict";
import { generateKeyPairSync, sign } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { updateReleaseChannelPointer } from "./update-release-channel-pointer.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const temp = await mkdtemp(path.join(os.tmpdir(), "kautuka-release-channel-pointer-"));
const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
const publicKeyPath = path.join(temp, "manifest-public-key.pem");
const pointerDirectory = path.join(temp, "release-channel");
await import("node:fs/promises").then(({ mkdir }) => mkdir(pointerDirectory));
await writeFile(publicKeyPath, publicKey.export({ type: "spki", format: "pem" }));

function manifest(channel, tag, overrides = {}) {
  const base = `https://github.com/devkrity/kautuka/releases/download/${tag}`;
  return {
    schemaVersion: 2,
    releaseId: tag,
    releaseTag: tag,
    channel,
    version: tag.slice(1),
    sourceSha: "0123456789abcdef0123456789abcdef01234567",
    publishedAt: "2026-10-29T00:00:00Z",
    signature: {
      algorithm: "ecdsa-p256-sha256",
      path: "kautuka-platform-release.sig",
      url: `${base}/kautuka-platform-release.sig`,
      format: "base64",
    },
    artifacts: [
      {
        name: "kautuka-linux-amd64.tar.gz",
        url: `${base}/kautuka-linux-amd64.tar.gz`,
        sha256: "a".repeat(64),
        size: 100,
      },
      {
        name: "kautuka-wsl-appliance.tar.gz",
        url: `${base}/kautuka-wsl-appliance.tar.gz`,
        sha256: "b".repeat(64),
        size: 200,
      },
    ],
    ...overrides,
  };
}

function signedManifest(document) {
  const bytes = Buffer.from(`${JSON.stringify(document, null, 2)}\n`);
  return { bytes, signature: `${sign("sha256", bytes, privateKey).toString("base64")}\n` };
}

async function putInput(name, bytes) {
  const file = path.join(temp, name);
  await writeFile(file, bytes);
  return file;
}

async function publish(channel, document, fetchImpl = async () => new Response("", { status: 404 })) {
  const signed = signedManifest(document);
  const manifestPath = await putInput(`${channel}-candidate.json`, signed.bytes);
  const signaturePath = await putInput(`${channel}-candidate.sig`, signed.signature);
  return updateReleaseChannelPointer({
    channel,
    manifestPath,
    signaturePath,
    publicKeyPath,
    pointerDirectory,
    fetchImpl,
  });
}

try {
  const firstPublicationDirectory = path.join(temp, "first-release-channel");
  await import("node:fs/promises").then(({ mkdir }) => mkdir(firstPublicationDirectory));
  const firstStable = signedManifest(manifest("stable", "v0.1.0"));
  const firstStableManifestPath = await putInput("first-stable.json", firstStable.bytes);
  const firstStableSignaturePath = await putInput("first-stable.sig", firstStable.signature);
  await updateReleaseChannelPointer({
    channel: "stable",
    manifestPath: firstStableManifestPath,
    signaturePath: firstStableSignaturePath,
    publicKeyPath,
    pointerDirectory: firstPublicationDirectory,
  });
  assert.deepEqual(await readFile(path.join(firstPublicationDirectory, "stable.json")), firstStable.bytes,
    "the first release must create the channel snapshot when the branch has no pointer file yet");

  const stableOld = signedManifest(manifest("stable", "v1.2.3"));
  await writeFile(path.join(pointerDirectory, "stable.json"), stableOld.bytes);
  const stableBefore = await readFile(path.join(pointerDirectory, "stable.json"));

  const previewFirst = manifest("preview", "v1.2.3-preview.4");
  const previewFirstSigned = signedManifest(previewFirst);
  await writeFile(path.join(pointerDirectory, "preview.json"), previewFirstSigned.bytes);
  const previewFirstSignature = previewFirstSigned.signature;

  const previewNew = manifest("preview", "v1.2.3-preview.5");
  const previewNewSigned = signedManifest(previewNew);
  const previewNewManifestPath = await putInput("preview-new.json", previewNewSigned.bytes);
  const previewNewSignaturePath = await putInput("preview-new.sig", previewNewSigned.signature);
  const signatureUrls = new Map([
    [`https://github.com/devkrity/kautuka/releases/download/v1.2.3-preview.4/kautuka-platform-release.sig`, previewFirstSignature],
  ]);
  const pointerPath = await updateReleaseChannelPointer({
    channel: "preview",
    manifestPath: previewNewManifestPath,
    signaturePath: previewNewSignaturePath,
    publicKeyPath,
    pointerDirectory,
    fetchImpl: async (url) => new Response(signatureUrls.get(url) ?? "", { status: signatureUrls.has(url) ? 200 : 404 }),
  });
  assert.equal(pointerPath, path.join(pointerDirectory, "preview.json"));
  assert.deepEqual(await readFile(pointerPath), previewNewSigned.bytes, "published pointer must preserve the exact signed manifest bytes");
  assert.deepEqual(await readFile(path.join(pointerDirectory, "stable.json")), stableBefore, "Preview publication must not change Stable pointer bytes");
  signatureUrls.set(
    "https://github.com/devkrity/kautuka/releases/download/v1.2.3-preview.5/kautuka-platform-release.sig",
    previewNewSigned.signature,
  );

  let idempotentFetchCount = 0;
  const idempotentPath = await updateReleaseChannelPointer({
    channel: "preview",
    manifestPath: previewNewManifestPath,
    signaturePath: previewNewSignaturePath,
    publicKeyPath,
    pointerDirectory,
    fetchImpl: async () => {
      idempotentFetchCount += 1;
      return new Response("", { status: 404 });
    },
  });
  assert.equal(idempotentPath, pointerPath, "retrying the already-published exact manifest must be a safe no-op");
  assert.equal(idempotentFetchCount, 0, "byte-identical pointer retries need no additional network request");

  const conflictingSameVersion = manifest("preview", "v1.2.3-preview.5", {
    sourceSha: "fedcba9876543210fedcba9876543210fedcba98",
  });
  await assert.rejects(
    publish("preview", conflictingSameVersion, async (url) => new Response(
      signatureUrls.get(url) ?? "",
      { status: signatureUrls.has(url) ? 200 : 404 },
    )),
    /non-newer/u,
    "a same-version manifest with different bytes must not replace the published snapshot",
  );

  const replayPath = await putInput("preview-replay.json", previewFirstSigned.bytes);
  const replaySigPath = await putInput("preview-replay.sig", previewFirstSignature);
  await assert.rejects(
    updateReleaseChannelPointer({
      channel: "preview",
      manifestPath: replayPath,
      signaturePath: replaySigPath,
      publicKeyPath,
      pointerDirectory,
      fetchImpl: async (url) => new Response(signatureUrls.get(url) ?? "", { status: signatureUrls.has(url) ? 200 : 404 }),
    }),
    /non-newer/u,
    "an older authentic signed manifest must not be replayed by the publisher",
  );

  const invalidChannelPath = await putInput("wrong-channel.json", signedManifest(manifest("stable", "v1.3.0")).bytes);
  const invalidChannelSignaturePath = await putInput("wrong-channel.sig", signedManifest(manifest("stable", "v1.3.0")).signature);
  await assert.rejects(
    updateReleaseChannelPointer({
      channel: "preview",
      manifestPath: invalidChannelPath,
      signaturePath: invalidChannelSignaturePath,
      publicKeyPath,
      pointerDirectory,
    }),
    /channel does not match/u,
  );

  const wrongSignatureUrl = manifest("preview", "v1.2.3-preview.6", {
    signature: { ...manifest("preview", "v1.2.3-preview.6").signature, url: "https://example.invalid/fake.sig" },
  });
  await assert.rejects(
    publish("preview", wrongSignatureUrl, async () => new Response("", { status: 404 })),
    /exact-tag/u,
  );

  const wrongArtifactUrl = manifest("preview", "v1.2.3-preview.6", {
    artifacts: manifest("preview", "v1.2.3-preview.6").artifacts.map((artifact) =>
      artifact.name === "kautuka-linux-amd64.tar.gz" ? { ...artifact, url: "https://example.invalid/fake.tar.gz" } : artifact),
  });
  await assert.rejects(
    publish("preview", wrongArtifactUrl),
    /exact-tag/u,
  );

  const tamperedCurrent = Buffer.from(previewNewSigned.bytes.toString("utf8").replace("0123456789abcdef0123456789abcdef01234567", "fedcba9876543210fedcba9876543210fedcba98"));
  await writeFile(pointerPath, tamperedCurrent);
  const previewNext = manifest("preview", "v1.2.3-preview.6");
  const previewNextSigned = signedManifest(previewNext);
  const previewNextManifestPath = await putInput("preview-next.json", previewNextSigned.bytes);
  const previewNextSignaturePath = await putInput("preview-next.sig", previewNextSigned.signature);
  await assert.rejects(
    updateReleaseChannelPointer({
      channel: "preview",
      manifestPath: previewNextManifestPath,
      signaturePath: previewNextSignaturePath,
      publicKeyPath,
      pointerDirectory,
      fetchImpl: async (url) => new Response(previewNewSigned.signature, { status: url.endsWith("/kautuka-platform-release.sig") ? 200 : 404 }),
    }),
    /signature verification failed/u,
    "a tampered current pointer must block any subsequent channel update",
  );

  const publishWorkflow = await readFile(path.join(root, ".github/workflows/release-publish-assets.yml"), "utf8");
  const releasePipeline = await readFile(path.join(root, ".github/workflows/release-pipeline.yml"), "utf8");
  const immutableIndex = publishWorkflow.indexOf("- name: Require GitHub immutable-release enforcement");
  const pointerCheckoutIndex = publishWorkflow.indexOf("- name: Check out the protected release-channel branch");
  const draftMutationIndex = publishWorkflow.indexOf("- name: Confirm release draft is empty and immutable-release compatible");
  const assetUploadIndex = publishWorkflow.indexOf("- name: Upload all versioned release assets to the GitHub draft");
  const releasePublishIndex = publishWorkflow.indexOf("- name: Publish immutable GitHub Release");
  const pointerPublishIndex = publishWorkflow.indexOf("- name: Advance only the selected signed channel pointer");
  assert.ok(pointerCheckoutIndex >= 0 && pointerCheckoutIndex < draftMutationIndex
    && pointerCheckoutIndex < assetUploadIndex && pointerCheckoutIndex < releasePublishIndex,
    "the publisher must verify release-channel exists before draft edits, asset uploads, or release publication");
  assert.ok(immutableIndex > releasePublishIndex && pointerPublishIndex > immutableIndex,
    "the matching pointer can advance only after GitHub reports immutable release publication");
  assert.match(publishWorkflow, /git -C release-channel add "\$CHANNEL\.json"/u,
    "the release job stages only the channel selected by its trusted channel input");
  assert.match(publishWorkflow, /ref: release-channel\n\s+path: release-channel\n\s+fetch-depth: 1\n\s+token: \$\{\{ github\.token \}\}\n\s+persist-credentials: true/u,
    "the pointer checkout must explicitly persist the contents-authorized workflow token for its normal fast-forward push");
  assert.match(publishWorkflow, /--channel "\$CHANNEL"/u);
  assert.match(releasePipeline, /uses: \.\/\.github\/workflows\/release-publish-assets\.yml\n\s+with:\n\s+channel: \$\{\{ needs\.resolve-channel\.outputs\.channel \}\}/u,
    "the publisher channel must come from trusted release-tag resolution, not a separate user-controlled input");
  const branchPreflight = releasePipeline.indexOf("  validate-release-channel-branch:\n");
  const draftPreparation = releasePipeline.indexOf("  prepare-stable-draft:\n");
  assert.ok(branchPreflight >= 0 && branchPreflight < draftPreparation,
    "the caller must preflight release-channel before it can create a draft");
  const branchPreflightJob = releasePipeline.slice(branchPreflight, draftPreparation);
  assert.match(branchPreflightJob, /ref: release-channel[\s\S]*?test -s release-channel\/README\.md/u,
    "preflight must check out the initialized branch and require its bootstrap README");
  const draftJob = releasePipeline.slice(draftPreparation, releasePipeline.indexOf("  publish-kautuka-base:\n"));
  assert.match(draftJob, /needs:[\s\S]*validate-release-channel-branch/u,
    "draft creation must depend on successful channel-branch preflight");
  const baseJob = releasePipeline.slice(releasePipeline.indexOf("  publish-kautuka-base:\n"), releasePipeline.indexOf("  # Native AOT"));
  assert.match(baseJob, /needs\.validate-release-channel-branch\.result == 'success'/u,
    "non-dry-run build/publication must not proceed if channel preflight fails");
  assert.match(baseJob, /dry_run == 'true' \|\| needs\.validate-release-channel-branch\.result == 'success'/u,
    "dry-run remains usable without mutating or requiring the publication branch");
  const assetsJob = releasePipeline.slice(releasePipeline.indexOf("  publish-assets:\n"));
  assert.match(assetsJob, /needs:[\s\S]*validate-release-channel-branch/u);
  assert.match(assetsJob, /needs\.validate-release-channel-branch\.result == 'success'/u,
    "release asset publication must require the preflight result");
  assert.doesNotMatch(publishWorkflow, /git -C release-channel add -A|git -C release-channel add \./u,
    "the workflow must not stage the other channel pointer");

  const linuxTemplate = await readFile(path.join(root, "scripts/kautuka-install-linux.sh.in"), "utf8");
  const windowsInstaller = await readFile(path.join(root, "src/installer/Kautuka.Installer.Windows/Installation/WindowsApplianceInstaller.Release.cs"), "utf8");
  const updater = await readFile(path.join(root, "src/backend/Kautuka.UpdateService/Discovery/ManifestUrlReleaseDiscoveryClient.cs"), "utf8");
  assert.doesNotMatch(linuxTemplate, /api\.github\.com|releases\?per_page/u);
  assert.doesNotMatch(windowsInstaller, /api\.github\.com|releases\?per_page/u);
  assert.doesNotMatch(updater, /api\.github\.com|releases\?per_page/u);

  console.log("Signed release-channel pointer regressions passed: exact manifest snapshots, signature and identity checks, monotonic semver, stale replay rejection, channel isolation, tamper rejection, and post-immutability publication ordering.");
} finally {
  await rm(temp, { recursive: true, force: true });
}
