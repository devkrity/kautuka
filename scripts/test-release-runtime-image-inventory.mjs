#!/usr/bin/env node

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { validateReleaseRuntimeImages, validateReviewedRuntimeInputs } from './validate-release-runtime-images.mjs';

const digest = 'a'.repeat(64);
const baseImage = `ghcr.io/devkrity/kautuka-base@sha256:${digest}`;
const runtimeImages = [
  { id: 'kautuka-base', image: baseImage, archive: 'kautuka-base.tar' },
  { id: 'openobserve', image: 'ghcr.io/devkrity/kautuka-observability@sha256:openobserve', archive: 'openobserve.tar' },
  { id: 'git-pkgs-proxy', image: 'ghcr.io/devkrity/kautuka-package-proxy@sha256:proxy', archive: 'git-pkgs-proxy.tar' },
];
const components = [
  { id: 'openobserve', image: runtimeImages[1].image },
  { id: 'git-pkgs-proxy', image: runtimeImages[2].image },
];

const sidecars = validateReleaseRuntimeImages(runtimeImages, components, baseImage);
assert.deepEqual(sidecars.map((image) => image.id), ['openobserve', 'git-pkgs-proxy'],
  'the pinned base appliance image is tracked separately from cataloged sidecars');

assert.throws(
  () => validateReleaseRuntimeImages(runtimeImages, components, `ghcr.io/devkrity/kautuka-base@sha256:${'b'.repeat(64)}`),
  /does not match the explicit --base-image/u,
  'the embedded base image must match the release-selected digest',
);
assert.throws(
  () => validateReleaseRuntimeImages(runtimeImages, components, undefined),
  /does not match the explicit --base-image/u,
  'a pinned runtime base image must not be omitted from the compliance inventory',
);
assert.throws(
  () => validateReleaseRuntimeImages(runtimeImages, [components[1]], baseImage),
  /catalog is missing or stale for runtime image openobserve/u,
  'uncataloged sidecars must still fail closed',
);
assert.throws(
  () => validateReleaseRuntimeImages([...runtimeImages, runtimeImages[0]], components, baseImage),
  /multiple kautuka-base entries/u,
  'duplicate base image entries must be rejected',
);

console.log('Release runtime image inventory tests passed: pinned base-image binding, sidecar inventory, and fail-closed mismatch handling.');

const json = async (relative) => JSON.parse(await readFile(new URL(relative, import.meta.url), 'utf8'));
const inputs = await json('../src/images/runtime-release-inputs.json');
const catalog = await json('../compliance/third-party-components.json');
validateReviewedRuntimeInputs(inputs, catalog.components);
for (const [id, key, version, commit] of [
  ['envoy', 'envoyImage', '1.40.2', 'b2d89e0ae53916f28770771fbdd49c5c99c5c5a2'],
  ['coredns', 'dnsImage', '1.14.6', '424d125775cd70fa90dfc80bf0e52cc9a9aeb574'],
]) {
  const component = catalog.components.find((item) => item.id === id);
  assert.equal(component.version, version);
  assert.equal(component.sourceCommit, commit);
  const file = `compliance/licenses/LICENSE-${id}.txt`;
  const bytes = await readFile(new URL(`../${file}`, import.meta.url));
  assert.equal(createHash('sha256').update(bytes).digest('hex'), component.artifacts[file]);
  assert.match(bytes.toString('utf8'), /Apache License/u);
  assert.throws(() => validateReviewedRuntimeInputs({ ...inputs, [key]: `${component.image.slice(0, -64)}${'b'.repeat(64)}` }, catalog.components), /Missing or stale/u);
  assert.throws(() => validateReviewedRuntimeInputs(inputs, catalog.components.filter((item) => item.id !== id)), /Missing or stale/u);
  assert.throws(() => validateReviewedRuntimeInputs(inputs, [...catalog.components, component]), /Missing or stale/u);
  for (const replacement of [{ sourceCommit: '' }, { source: 'https://github.com/unrelated/project' }, { artifacts: {} }]) {
    assert.throws(() => validateReviewedRuntimeInputs(inputs, catalog.components.map((item) => item.id === id ? { ...item, ...replacement } : item)), /Missing or stale/u);
  }
  const generator = await readFile(new URL('./generate-third-party-compliance.mjs', import.meta.url), 'utf8');
  assert.ok(generator.includes(`'LICENSE-${id}.txt'`), 'release generator must copy the reviewed license');
  assert.ok(generator.includes('validateReviewedRuntimeInputs(runtimeInputs, catalog.components)'), 'generation must validate current producer pins');
}
console.log('Envoy/CoreDNS top-level producer pins, measured source identities, license bytes and drift rejection passed.');
const envoy = catalog.components.find((item) => item.id === 'envoy');
const noticeBytes = await readFile(new URL('../compliance/licenses/NOTICE-envoy.txt', import.meta.url));
assert.equal(createHash('sha256').update(noticeBytes).digest('hex'), envoy.artifacts['compliance/licenses/NOTICE-envoy.txt']);
assert.equal(envoy.artifacts['compliance/licenses/NOTICE-envoy.txt'], '329d90738f1ee3633da8e7439ea6f9624ec6274a9cbd72437aafa0e8df9de32e');
assert.throws(() => validateReviewedRuntimeInputs(inputs, catalog.components.map((item) => item.id === 'envoy' ? { ...item, noticeFiles: [] } : item)), /Missing Envoy upstream NOTICE/u);
assert.ok((await readFile(new URL('./generate-third-party-compliance.mjs', import.meta.url), 'utf8')).includes("'NOTICE-envoy.txt'"));
