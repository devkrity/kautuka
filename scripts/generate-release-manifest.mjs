#!/usr/bin/env node

import { createHash, createPrivateKey, sign as cryptoSign } from "node:crypto";
import { createReadStream } from "node:fs";
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
  const hash = createHash("sha256");
  let size = 0;
  for await (const chunk of createReadStream(fullPath)) {
    hash.update(chunk);
    size += chunk.length;
  }
  return {
    name,
    platform,
    path: relativePath,
    sha256: hash.digest("hex"),
    size,
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

const applianceServiceRoles = ["credentialshost", "channelhost", "gateway", "webhost", "updateservice"];
const registryLabel = "[a-z0-9](?:[a-z0-9-]*[a-z0-9])?";
const repositorySegment = "[a-z0-9]+(?:(?:[.]|__?|[-]+)[a-z0-9]+)*";
const pinnedImage = new RegExp(
  `^(?:localhost|${registryLabel}(?:[.]${registryLabel})+)(?::([1-9][0-9]{0,4}))?(?:/${repositorySegment})+@sha256:[a-f0-9]{64}$`,
  "u",
);

function hasFields(value, fields) {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === fields.length
    && fields.every((field) => Object.hasOwn(value, field));
}

async function loadApplianceDeployment(deploymentPath) {
  const deploymentBytes = await fs.readFile(path.resolve(deploymentPath));
  if (deploymentBytes.length > 16 * 1024) {
    throw new Error("Appliance deployment descriptor exceeds 16 KiB.");
  }
  const deployment = JSON.parse(deploymentBytes.toString("utf8"));
  if (!hasFields(deployment, ["schemaVersion", "hookAbi", "services"])
      || deployment.schemaVersion !== 1 || !Number.isInteger(deployment.hookAbi)
      || deployment.hookAbi < 1 || deployment.hookAbi > 2147483647) {
    throw new Error("Appliance deployment requires schemaVersion 1, a positive hookAbi, and services.");
  }
  if (!hasFields(deployment.services, applianceServiceRoles)) {
    throw new Error("Appliance deployment must declare exactly the five supported service roles.");
  }
  for (const role of applianceServiceRoles) {
    const service = deployment.services[role];
    const image = service?.image;
    const match = typeof image === "string" && image.length <= 512 && pinnedImage.exec(image);
    if (!hasFields(service, ["image"]) || !match || match[0] !== image || Number(match[1] ?? 0) > 65535) {
      throw new Error(`Appliance service '${role}' requires a canonical digest-pinned OCI image reference.`);
    }
  }
  return deployment;
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

  const linuxApplianceOnly = options["linux-appliance-only"] === "true";
  if (linuxApplianceOnly && (options.channel !== "preview" || !options["appliance-deployment"])) {
    throw new Error("Linux appliance-only releases require the preview channel and an appliance deployment descriptor.");
  }
  const artifacts = [
    await fileMetadata(
      outputDir,
      "kautuka-linux-amd64.tar.gz",
      "kautuka-linux-amd64.tar.gz",
      "linux-x64",
    ),
  ];
  if (!linuxApplianceOnly) {
    artifacts.push(await fileMetadata(
      outputDir,
      "kautuka-wsl-appliance.tar.gz",
      "kautuka-wsl-appliance.tar.gz",
      "wsl-amd64",
    ));
  }

  const runtimeImages = await loadRuntimeImages(options["runtime-images"]);
  const applianceDeployment = options["appliance-deployment"]
    ? await loadApplianceDeployment(options["appliance-deployment"])
    : undefined;

  const manifest = stable({
    schemaVersion: 2,
    releaseId: options["release-id"] ?? null,
    releaseTag: options["release-tag"],
    channel: options.channel,
    version: options.version,
    sourceSha: options["source-sha"],
    publishedAt,
    minimumUpdaterVersion: options["minimum-updater-version"] || null,
    artifacts: artifacts.map((artifact) => ({
      ...artifact,
      url: artifactUrlFor(artifact.path, artifactBaseUrl),
    })),
    runtimeImages,
    applianceDeployment,
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
  if (privateKey.asymmetricKeyType !== "ec" || privateKey.asymmetricKeyDetails?.namedCurve !== "prime256v1") {
    throw new Error("Release manifest signing key must be an ECDSA P-256 private key.");
  }
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
