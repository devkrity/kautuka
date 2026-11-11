#!/usr/bin/env node

import assert from 'node:assert/strict';
import { validateReleaseRuntimeImages } from './validate-release-runtime-images.mjs';

const digest = 'a'.repeat(64);
const baseImage = `ghcr.io/devkrity/kautuka-base@sha256:${digest}`;
const runtimeImages = [
  { id: 'kautuka-base', image: baseImage, archive: 'kautuka-base.tar' },
  { id: 'openobserve', image: 'ghcr.io/devkrity/kautuka-observability@sha256:openobserve', archive: 'openobserve.tar' },
  { id: 'git-pkgs-proxy', image: 'ghcr.io/devkrity/kautuka-package-proxy@sha256:proxy', archive: 'git-pkgs-proxy.tar' },
  { id: 'envoy', image: 'docker.io/envoyproxy/envoy@sha256:envoy', archive: null },
  { id: 'coredns', image: 'docker.io/coredns/coredns@sha256:coredns', archive: null },
];
const components = [
  { id: 'openobserve', image: runtimeImages[1].image },
  { id: 'git-pkgs-proxy', image: runtimeImages[2].image },
  { id: 'envoy', image: runtimeImages[3].image },
  { id: 'coredns', image: runtimeImages[4].image },
];

const sidecars = validateReleaseRuntimeImages(runtimeImages, components, baseImage);
assert.deepEqual(sidecars.map((image) => image.id), ['openobserve', 'git-pkgs-proxy', 'envoy', 'coredns'],
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
