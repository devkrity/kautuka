#!/usr/bin/env node

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const template = await readFile(path.join(root, "scripts/kautuka-install-linux.sh.in"), "utf8");
assert.match(template, /api\.github\.com\/repos\/\{owner\}\/\{repository\}\/releases\?per_page=100&page=/u,
  "Linux bootstrap must discover releases from the public GitHub Releases API");

const resolverStart = template.indexOf("resolve_github_release_candidate() {");
const resolverEnd = template.indexOf("\nwrite_update_source_document()", resolverStart);
assert.ok(resolverStart >= 0 && resolverEnd > resolverStart, "Linux installer must provide GitHub Release discovery");
const resolver = template.slice(resolverStart, resolverEnd);
const temp = await mkdtemp(path.join(os.tmpdir(), "kautuka-linux-release-discovery-"));
const pythonMock = `import io, json, os, urllib.parse, urllib.request\ndef mocked(request, timeout=None):\n    page = urllib.parse.parse_qs(urllib.parse.urlparse(request.full_url).query).get('page', ['1'])[0]\n    pages = json.loads(os.environ.get('MOCK_GH_RELEASE_PAGES', '{}'))\n    return io.BytesIO(json.dumps(pages.get(page, [])).encode())\nurllib.request.urlopen = mocked\n`;
await writeFile(path.join(temp, "sitecustomize.py"), pythonMock);
const runResolver = (channel, pages) => spawnSync("bash", ["-c", `fail(){ echo "$*" >&2; return 1; }; ${resolver}\nresolve_github_release_candidate ${channel} devkrity kautuka`], {
  encoding: "utf8",
  env: { ...process.env, PYTHONPATH: temp, MOCK_GH_RELEASE_PAGES: JSON.stringify(pages) },
});
const release = (tag, { draft = false, prerelease = tag.includes("-preview."), published = true } = {}) => ({
  tag_name: tag, draft, prerelease, published_at: published ? "2026-10-29T00:00:00Z" : null,
});
const stable = runResolver("stable", { "1": [release("v1.0.1"), release("v2.0.0"), release("v9.0.0-preview.1"), release("v3.0.0", { draft: true }), release("v3.0.0", { published: false }), release("prefix-v8.0.0")] });
assert.equal(stable.status, 0, stable.stderr);
assert.equal(stable.stdout.trim(), "v2.0.0\thttps://github.com/devkrity/kautuka/releases/download/v2.0.0/kautuka-platform-release.json");
const preview = runResolver("preview", { "1": [release("v1.0.0-preview.9"), release("v1.0.0-preview.10")] });
assert.equal(preview.status, 0, preview.stderr);
assert.match(preview.stdout.trim(), /^v1\.0\.0-preview\.10\t/u);
const duplicate = runResolver("preview", { "1": [release("v1.0.0-preview.1"), release("v1.0.0-preview.1")] });
assert.notEqual(duplicate.status, 0);
assert.match(duplicate.stderr, /multiple published preview releases claim the same version/u);
const malformed = runResolver("stable", { "1": [{ tag_name: "v1.0.0", draft: "false", prerelease: false, published_at: "2026-10-29T00:00:00Z" }] });
assert.notEqual(malformed.status, 0);
assert.match(malformed.stderr, /metadata is malformed/u);
const exactThousandPages = Object.fromEntries(Array.from({ length: 10 }, (_, page) => [String(page + 1), Array.from({ length: 100 }, (_, index) => release(`v1.0.${page * 100 + index}`))]));
const exactThousand = runResolver("stable", exactThousandPages);
assert.equal(exactThousand.status, 0, exactThousand.stderr);
assert.match(exactThousand.stdout, /^v1\.0\.999\t/u);
const overThousand = runResolver("stable", { ...exactThousandPages, "11": [release("v2.0.0")] });
assert.notEqual(overThousand.status, 0);
assert.match(overThousand.stderr, /exceeds the safe 1,000-release/u);

const parserMarker = 'manifest_identity="$(python3 - ';
const parserMarkerIndex = template.indexOf(parserMarker);
const heredocStartIndex = template.indexOf("<<'PY'\n", parserMarkerIndex);
const parserStart = heredocStartIndex + "<<'PY'\n".length;
const parserEnd = template.indexOf("\nPY\n", parserStart);
assert.ok(parserMarkerIndex >= 0 && heredocStartIndex > parserMarkerIndex && parserEnd > parserStart,
  "installer must validate selected pointer manifest identity before downloading selected assets");
const manifestParser = template.slice(parserStart, parserEnd);
try {
  const makeManifest = (channel, tag, overrides = {}) => ({
    releaseId: tag,
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
  assert.match(substitutedSignature.stderr, /not its exact-tag asset/u);

  const substitutedArtifact = await run("preview", makeManifest("preview", "v1.2.3-preview.4", {
    artifacts: [{ name: "kautuka-linux-amd64.tar.gz", url: "https://example.invalid/archive" }],
  }));
  assert.notEqual(substitutedArtifact.status, 0);
  assert.match(substitutedArtifact.stderr, /not its exact-tag asset/u);

  console.log("Linux GitHub Release discovery regressions passed: strict channel selection, highest SemVer, duplicate rejection, exact 1,000 cap, and signed-manifest exact-tag validation.");
} finally {
  await rm(temp, { recursive: true, force: true });
}
