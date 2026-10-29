#!/usr/bin/env node

import { createHash, createPrivateKey, sign as cryptoSign } from "node:crypto";
import { promises as fs } from "node:fs";
import path from "node:path";

function parseArgs(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith("--")) {
      throw new Error(`Unexpected argument: ${token}`);
    }

    const key = token.slice(2);
    const value = argv[index + 1];
    if (!value || value.startsWith("--")) {
      throw new Error(`Missing value for --${key}`);
    }

    options[key] = value;
    index += 1;
  }

  return options;
}

function stable(value) {
  if (Array.isArray(value)) {
    return value.map(stable);
  }

  if (value && typeof value === "object") {
    return Object.keys(value)
      .sort()
      .reduce((accumulator, key) => {
        accumulator[key] = stable(value[key]);
        return accumulator;
      }, {});
  }

  return value;
}

async function fileMetadata(rootDir, relativePath, name, platform) {
  const fullPath = path.join(rootDir, relativePath);
  const fileBytes = await fs.readFile(fullPath);
  const stats = await fs.stat(fullPath);
  return {
    name,
    platform,
    path: relativePath,
    sha256: createHash("sha256").update(fileBytes).digest("hex").toLowerCase(),
    size: stats.size,
  };
}

function normalizeBaseUrl(value) {
  return value ? value.replace(/\/+$/u, "") : null;
}

function artifactUrlFor(relativePath, artifactBaseUrl) {
  const normalizedBaseUrl = normalizeBaseUrl(artifactBaseUrl);
  return normalizedBaseUrl ? `${normalizedBaseUrl}/${relativePath}` : null;
}

async function loadRuntimeImages(runtimeImagesPath) {
  if (!runtimeImagesPath) {
    return [];
  }

  const raw = await fs.readFile(path.resolve(runtimeImagesPath), "utf8");
  const parsed = JSON.parse(raw);
  if (!Array.isArray(parsed?.images)) {
    throw new Error(
      `runtime-images file must contain an images array: ${runtimeImagesPath}`,
    );
  }

  return parsed.images.map((entry) => {
    if (!entry?.id || !entry?.image) {
      throw new Error(
        `runtime-images entries require id and image: ${runtimeImagesPath}`,
      );
    }

    return stable({
      id: entry.id,
      image: entry.image,
      archive: entry.archive ?? null,
    });
  });
}

function signatureUrlFor(signatureName, signatureBaseUrl, artifactBaseUrl) {
  const normalizedSignatureBaseUrl = normalizeBaseUrl(signatureBaseUrl);
  if (normalizedSignatureBaseUrl) {
    return `${normalizedSignatureBaseUrl}/${signatureName}`;
  }

  return artifactUrlFor(signatureName, artifactBaseUrl);
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const outputDir = path.resolve(options["output-dir"] ?? "");
  const privateKeyPath = path.resolve(options["private-key"] ?? "");
  if (!options["output-dir"] || !options["private-key"]) {
    throw new Error(
      "Required arguments: --output-dir <dir> --private-key <pem>",
    );
  }

  const manifestName =
    options["manifest-name"] ?? "kautuka-platform-release.json";
  const signatureName =
    options["signature-name"] ?? "kautuka-platform-release.sig";
  const publishedAt = options["published-at"] ?? new Date().toISOString();
  const artifactBaseUrl = options["artifact-base-url"] ?? null;
  const signatureBaseUrl = options["signature-base-url"] ?? null;
  const signatureUrlOverride = options["signature-url"] ?? null;
  if (signatureUrlOverride) {
    let parsedSignatureUrl;
    try {
      parsedSignatureUrl = new URL(signatureUrlOverride);
    } catch {
      throw new Error("--signature-url must be an absolute http(s) URL");
    }
    if (!["http:", "https:"].includes(parsedSignatureUrl.protocol)) {
      throw new Error("--signature-url must be an absolute http(s) URL");
    }
  }

  const artifacts = [
    await fileMetadata(
      outputDir,
      "kautuka-linux-amd64.tar.gz",
      "kautuka-linux-amd64.tar.gz",
      "linux-x64",
    ),
    await fileMetadata(
      outputDir,
      "kautuka-wsl-appliance.tar.gz",
      "kautuka-wsl-appliance.tar.gz",
      "wsl-amd64",
    ),
  ].map((artifact) => ({
    ...artifact,
    url: artifactUrlFor(artifact.path, artifactBaseUrl),
  }));

  const runtimeImages = await loadRuntimeImages(options["runtime-images"]);

  const manifest = stable({
    schemaVersion: 2,
    releaseId: options["release-id"] ?? null,
    releaseTag: options["release-tag"],
    channel: options.channel,
    version: options.version,
    sourceSha: options["source-sha"],
    publishedAt,
    minimumUpdaterVersion: options["minimum-updater-version"] || null,
    artifacts,
    runtimeImages,
    signature: {
      algorithm: "ecdsa-p256-sha256",
      path: signatureName,
      url:
        signatureUrlOverride ??
        signatureUrlFor(signatureName, signatureBaseUrl, artifactBaseUrl),
      format: "base64",
    },
  });

  const manifestBytes = Buffer.from(
    `${JSON.stringify(manifest, null, 2)}\n`,
    "utf8",
  );
  const privateKeyPem = await fs.readFile(privateKeyPath, "utf8");
  const privateKey = createPrivateKey(privateKeyPem);
  const signature = cryptoSign("sha256", manifestBytes, privateKey).toString(
    "base64",
  );

  await fs.writeFile(path.join(outputDir, manifestName), manifestBytes);
  await fs.writeFile(
    path.join(outputDir, signatureName),
    `${signature}\n`,
    "utf8",
  );

  process.stdout.write(`${path.join(outputDir, manifestName)}\n`);
}

main().catch((error) => {
  process.stderr.write(
    `${error instanceof Error ? error.message : String(error)}\n`,
  );
  process.exitCode = 1;
});
