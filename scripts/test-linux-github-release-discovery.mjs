#!/usr/bin/env node

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const template = await readFile(path.join(root, "scripts/kautuka-install-linux.sh.in"), "utf8");
assert.doesNotMatch(template, /api\.github\.com|releases\?per_page|git\/ref\/tags/u,
  "Linux bootstrap must not scan the anonymous GitHub Releases API");

const resolverStart = template.indexOf("resolve_github_release_candidate() {");
const resolverEnd = template.indexOf("\nwrite_update_source_document()", resolverStart);
assert.ok(resolverStart >= 0 && resolverEnd > resolverStart, "Linux installer must provide channel pointer URL resolution");
const resolver = template.slice(resolverStart, resolverEnd);
for (const channel of ["stable", "preview"]) {
  const resolverResult = spawnSync("bash", ["-c", `fail(){ echo "$*" >&2; return 1; }; ${resolver}\nresolve_github_release_candidate ${channel} devkrity kautuka`], { encoding: "utf8" });
  assert.equal(resolverResult.status, 0, resolverResult.stderr);
  assert.equal(resolverResult.stdout.trim(), `https://raw.githubusercontent.com/devkrity/kautuka/release-channel/${channel}.json`);
}

const parserMarker = 'manifest_identity="$(python3 - ';
const parserMarkerIndex = template.indexOf(parserMarker);
const heredocStartIndex = template.indexOf("<<'PY'\n", parserMarkerIndex);
const parserStart = heredocStartIndex + "<<'PY'\n".length;
const parserEnd = template.indexOf("\nPY\n", parserStart);
assert.ok(parserMarkerIndex >= 0 && heredocStartIndex > parserMarkerIndex && parserEnd > parserStart,
  "installer must validate selected pointer manifest identity before downloading selected assets");
const manifestParser = template.slice(parserStart, parserEnd);
const temp = await mkdtemp(path.join(os.tmpdir(), "kautuka-linux-channel-pointer-"));

try {
  const makeManifest = (channel, tag, overrides = {}) => ({
    releaseTag: tag,
    channel,
    version: tag.slice(1),
    sourceSha: "0123456789abcdef0123456789abcdef01234567",
    signature: { url: `https://github.com/devkrity/kautuka/releases/download/${tag}/kautuka-platform-release.sig` },
    artifacts: [{
      name: "kautuka-linux-amd64.tar.gz",
      url: `https://github.com/devkrity/kautuka/releases/download/${tag}/kautuka-linux-amd64.tar.gz`,
    }],
    ...overrides,
  });
  const run = async (channel, doc) => {
    const manifestPath = path.join(temp, `${channel}.json`);
    await writeFile(manifestPath, JSON.stringify(doc));
    return spawnSync("python3", ["-c", manifestParser, manifestPath, channel, "devkrity", "kautuka", "true"], { encoding: "utf8" });
  };

  const preview = await run("preview", makeManifest("preview", "v1.2.3-preview.4"));
  assert.equal(preview.status, 0, preview.stderr);
  assert.deepEqual(preview.stdout.trim().split("\t"), [
    "v1.2.3-preview.4",
    "1.2.3-preview.4",
    "0123456789abcdef0123456789abcdef01234567",
    "https://github.com/devkrity/kautuka/releases/download/v1.2.3-preview.4/kautuka-platform-release.sig",
    "https://github.com/devkrity/kautuka/releases/download/v1.2.3-preview.4/kautuka-linux-amd64.tar.gz",
    "preview",
  ]);

  const stable = await run("stable", makeManifest("stable", "v1.3.0"));
  assert.equal(stable.status, 0, stable.stderr);
  assert.equal(stable.stdout.trim().split("\t")[0], "v1.3.0");
  assert.equal(stable.stdout.trim().split("\t")[5], "stable");

  const wrongChannel = await run("stable", makeManifest("preview", "v1.2.3-preview.4"));
  assert.notEqual(wrongChannel.status, 0);
  assert.match(wrongChannel.stderr, /different release channel/u);

  const malformedTag = await run("preview", makeManifest("preview", "v1.2.3-preview.04"));
  assert.notEqual(malformedTag.status, 0);
  assert.match(malformedTag.stderr, /strict stable\/preview version/u);

  const substitutedSignature = await run("preview", makeManifest("preview", "v1.2.3-preview.4", {
    signature: { url: "https://example.invalid/sig" },
  }));
  assert.notEqual(substitutedSignature.status, 0);
  assert.match(substitutedSignature.stderr, /not the exact-tag GitHub Release asset/u);

  const substitutedArtifact = await run("preview", makeManifest("preview", "v1.2.3-preview.4", {
    artifacts: [{ name: "kautuka-linux-amd64.tar.gz", url: "https://example.invalid/archive" }],
  }));
  assert.notEqual(substitutedArtifact.status, 0);
  assert.match(substitutedArtifact.stderr, /not the exact-tag GitHub Release asset/u);

  console.log("Linux channel pointer discovery regressions passed: fixed selected-channel raw URL, strict signed-manifest identity parsing, and exact-tag signature/artifact URL validation without Releases API calls.");
} finally {
  await rm(temp, { recursive: true, force: true });
}
