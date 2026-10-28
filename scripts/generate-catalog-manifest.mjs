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

function normalizeBaseUrl(value) {
  return value ? value.replace(/\/+$/u, "") : null;
}

async function sha256File(filePath) {
  if (!filePath) {
    return null;
  }

  const bytes = await fs.readFile(path.resolve(filePath));
  return createHash("sha256").update(bytes).digest("hex");
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

  const manifestName = options["manifest-name"] ?? "manifest.json";
  const signatureName = options["signature-name"] ?? "manifest.sig";
  const generatedAt = options["generated-at"] ?? new Date().toISOString();
  const manifestBaseUrl = normalizeBaseUrl(options["manifest-base-url"] ?? null);
  const connectorArtifactFile = options["connector-artifact-file"] ?? null;
  const connectorArtifactPath = options["connector-artifact-path"] ?? null;
  const docsArtifactFile = options["docs-artifact-file"] ?? null;
  const docsArtifactPath = options["docs-artifact-path"] ?? null;
  const connectorArtifactSha256 = await sha256File(connectorArtifactFile);
  const docsArtifactSha256 = await sha256File(docsArtifactFile);

  const resolveArtifactUrl = (artifactPath) =>
    manifestBaseUrl && artifactPath ? `${manifestBaseUrl}/${artifactPath}` : null;

  const manifest = stable({
    schemaVersion: 1,
    generatedAtUtc: generatedAt,
    entries: [
      {
        id: "kautuka-web-tools",
        kind: "connector",
        title: "Kautuka Web Tools",
        summary:
          "Built-in browser and page-inspection tools that help agents work with web content and interactive pages.",
        publisher: "Kautuka",
        version: "built-in",
        artifactUrl: resolveArtifactUrl(connectorArtifactPath),
        artifactSha256: connectorArtifactSha256,
        trustKind: "verified_by_kautuka",
        recommended: true,
      },
      {
        id: "kautuka-docs",
        kind: "skill",
        title: "Kautuka Docs",
        summary:
          "Built-in product guidance and reference material you can attach when you want the assistant grounded in Kautuka itself.",
        publisher: "Kautuka",
        version: "built-in",
        artifactUrl: resolveArtifactUrl(docsArtifactPath),
        artifactSha256: docsArtifactSha256,
        trustKind: "verified_by_kautuka",
        recommended: true,
      },
    ],
    signature: {
      algorithm: "ecdsa-p256-sha256",
      path: signatureName,
      url: manifestBaseUrl ? `${manifestBaseUrl}/${signatureName}` : null,
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

  await fs.mkdir(outputDir, { recursive: true });
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