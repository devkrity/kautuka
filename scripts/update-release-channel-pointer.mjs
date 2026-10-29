#!/usr/bin/env node

import { readFile, rename, writeFile } from "node:fs/promises";
import path from "node:path";
import { createPublicKey, verify as verifySignature } from "node:crypto";
import { pathToFileURL } from "node:url";

const releaseArtifactNames = {
  linux: "kautuka-linux-amd64.tar.gz",
  manifest: "kautuka-platform-release.json",
  signature: "kautuka-platform-release.sig",
};

function parseArgs(args) {
  const values = new Map();
  for (let index = 0; index < args.length; index += 1) {
    const name = args[index];
    if (!name.startsWith("--") || index + 1 >= args.length) {
      throw new Error(`Invalid argument near '${name}'.`);
    }
    values.set(name.slice(2), args[++index]);
  }
  return values;
}

function parseVersion(tag, channel) {
  const pattern = channel === "stable"
    ? /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/u
    : /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-preview\.(0|[1-9]\d*)$/u;
  const match = pattern.exec(tag ?? "");
  if (!match) throw new Error(`Release tag '${tag}' is not a strict ${channel} version.`);
  const parts = match.slice(1).map((part) => BigInt(part));
  return { parts, version: tag.slice(1) };
}

function compareVersions(left, right) {
  const leftParsed = parseVersion(left.releaseTag, left.channel);
  const rightParsed = parseVersion(right.releaseTag, right.channel);
  for (let index = 0; index < leftParsed.parts.length; index += 1) {
    const comparison = leftParsed.parts[index] < rightParsed.parts[index]
      ? -1
      : leftParsed.parts[index] > rightParsed.parts[index] ? 1 : 0;
    if (comparison !== 0) return comparison;
  }
  return 0;
}

function expectedUrls(document, owner, repository) {
  const tag = document.releaseTag;
  const base = `https://github.com/${owner}/${repository}/releases/download/${encodeURIComponent(tag)}`;
  return {
    manifest: `${base}/${releaseArtifactNames.manifest}`,
    signature: `${base}/${releaseArtifactNames.signature}`,
    linux: `${base}/${releaseArtifactNames.linux}`,
    wsl: `${base}/kautuka-wsl-appliance.tar.gz`,
  };
}

function validateManifest(document, channel, owner, repository) {
  if (!document || typeof document !== "object" || Array.isArray(document)) {
    throw new Error("Channel manifest must be a JSON object.");
  }
  if (document.schemaVersion !== 2) throw new Error("Channel manifest schema version is unsupported.");
  if (document.channel !== channel) throw new Error(`Manifest channel does not match '${channel}'.`);
  const parsed = parseVersion(document.releaseTag, channel);
  if (document.version !== parsed.version) throw new Error("Manifest version does not match its strict release tag.");
  if (typeof document.sourceSha !== "string" || !/^[0-9a-f]{40}$/iu.test(document.sourceSha)) {
    throw new Error("Manifest sourceSha must be a full Git commit SHA.");
  }
  const urls = expectedUrls(document, owner, repository);
  if (document.signature?.algorithm !== "ecdsa-p256-sha256"
      || document.signature?.format !== "base64"
      || document.signature?.path !== releaseArtifactNames.signature
      || document.signature?.url !== urls.signature) {
    throw new Error("Manifest signature metadata is not bound to the exact-tag GitHub Release asset.");
  }
  if (document.releaseId != null && document.releaseId !== document.releaseTag) {
    throw new Error("Manifest releaseId does not match its exact release tag.");
  }
  const artifacts = document.artifacts;
  if (!Array.isArray(artifacts)) throw new Error("Manifest artifact list is missing.");
  for (const [name, expectedUrl] of [
    [releaseArtifactNames.linux, urls.linux],
    ["kautuka-wsl-appliance.tar.gz", urls.wsl],
  ]) {
    const matches = artifacts.filter((artifact) => artifact?.name === name);
    if (matches.length !== 1 || matches[0].url !== expectedUrl) {
      throw new Error(`Manifest artifact '${name}' is not the exact-tag GitHub Release asset.`);
    }
  }
  return parsed;
}

function verifyManifestBytes(bytes, signatureText, publicKeyPem) {
  const signature = Buffer.from(signatureText.trim(), "base64");
  if (signature.length === 0 || !verifySignature("sha256", bytes, createPublicKey(publicKeyPem), signature)) {
    throw new Error("Release channel manifest signature verification failed.");
  }
}

async function fetchSignature(url, fetchImpl) {
  const response = await fetchImpl(url);
  if (!response.ok) throw new Error(`Could not retrieve the current exact-tag signature (HTTP ${response.status}).`);
  return response.text();
}

export async function updateReleaseChannelPointer({
  channel,
  manifestPath,
  signaturePath,
  publicKeyPath,
  pointerDirectory,
  owner = "devkrity",
  repository = "kautuka",
  fetchImpl = fetch,
}) {
  if (channel !== "stable" && channel !== "preview") throw new Error("Channel must be stable or preview.");
  const manifestBytes = await readFile(manifestPath);
  const signatureText = await readFile(signaturePath, "utf8");
  const publicKeyPem = await readFile(publicKeyPath, "utf8");
  const candidate = JSON.parse(manifestBytes.toString("utf8"));
  validateManifest(candidate, channel, owner, repository);
  verifyManifestBytes(manifestBytes, signatureText, publicKeyPem);

  const pointerPath = path.join(pointerDirectory, `${channel}.json`);
  let currentBytes;
  try {
    currentBytes = await readFile(pointerPath);
  } catch (error) {
    if (error?.code !== "ENOENT") throw error;
  }

  if (currentBytes) {
    if (currentBytes.equals(manifestBytes)) return pointerPath;

    const current = JSON.parse(currentBytes.toString("utf8"));
    const currentParsed = validateManifest(current, channel, owner, repository);
    const currentUrls = expectedUrls(current, owner, repository);
    const currentSignature = await fetchSignature(currentUrls.signature, fetchImpl);
    verifyManifestBytes(currentBytes, currentSignature, publicKeyPem);
    const comparison = compareVersions(candidate, current);
    if (comparison <= 0) {
      throw new Error(`Refusing to move ${channel} pointer from ${currentParsed.version} to non-newer ${candidate.version}.`);
    }
  }

  const temporaryPointerPath = `${pointerPath}.tmp-${process.pid}`;
  await writeFile(temporaryPointerPath, manifestBytes, { flag: "wx" });
  await rename(temporaryPointerPath, pointerPath);
  const writtenBytes = await readFile(pointerPath);
  if (!writtenBytes.equals(manifestBytes)) throw new Error("Channel pointer write did not preserve the signed manifest bytes.");
  return pointerPath;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const required = ["channel", "manifest", "signature", "public-key", "pointer-directory"];
  for (const name of required) if (!args.has(name)) throw new Error(`Missing --${name}.`);
  const pointerPath = await updateReleaseChannelPointer({
    channel: args.get("channel"),
    manifestPath: args.get("manifest"),
    signaturePath: args.get("signature"),
    publicKeyPath: args.get("public-key"),
    pointerDirectory: args.get("pointer-directory"),
    owner: args.get("owner") ?? "devkrity",
    repository: args.get("repository") ?? "kautuka",
  });
  process.stdout.write(`Updated ${path.basename(pointerPath)} with a verified newer signed release manifest.\n`);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  main().catch((error) => {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  });
}
