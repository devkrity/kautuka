const baseImagePattern = /^ghcr\.io\/devkrity\/kautuka-base@sha256:[a-f0-9]{64}$/u;

// Deliberately scoped to the two reviewed top-level upstream projects, not
// approval of other producer inputs or of these images' transitive packages.
export function validateReviewedRuntimeInputs(inputs, components) {
  for (const [id, key, repository] of [
    ['envoy', 'envoyImage', 'envoyproxy/envoy'],
    ['coredns', 'dnsImage', 'coredns/coredns'],
  ]) {
    const matches = components.filter((component) => component.id === id);
    const component = matches[0];
    if (matches.length !== 1 || !/@sha256:[a-f0-9]{64}$/u.test(inputs?.[key] ?? '')
        || component.image !== inputs[key]
        || !/^[a-f0-9]{40}$/u.test(component.sourceCommit ?? '')
        || component.source !== `https://github.com/${repository}/tree/${component.sourceCommit}`
        || !/^\d+\.\d+\.\d+$/u.test(component.version ?? '')
        || component.licenseExpression !== 'Apache-2.0'
        || !component.licenseFiles?.includes(`LICENSE-${id}.txt`)
        || !/^[a-f0-9]{64}$/u.test(component.artifacts?.[`compliance/licenses/LICENSE-${id}.txt`] ?? '')) {
      throw new Error(`Missing or stale top-level source/license evidence for runtime input ${key}; review the exact pinned ${id} image.`);
    }
    if (id === 'envoy' && (!component.noticeFiles?.includes('NOTICE-envoy.txt')
        || !/^[a-f0-9]{64}$/u.test(component.artifacts?.['compliance/licenses/NOTICE-envoy.txt'] ?? ''))) {
      throw new Error('Missing Envoy upstream NOTICE evidence.');
    }
  }
}

export function validateReleaseRuntimeImages(runtimeImages, components, selectedBaseImage) {
  if (!Array.isArray(runtimeImages) || !Array.isArray(components)) {
    throw new Error('Runtime images and third-party components must both be arrays.');
  }

  const baseImageEntries = runtimeImages.filter((image) => image?.id === 'kautuka-base');
  if (baseImageEntries.length > 1) {
    throw new Error('Runtime image inventory contains multiple kautuka-base entries.');
  }

  const baseImageEntry = baseImageEntries[0];
  const sidecarImages = runtimeImages.filter((image) => image?.id !== 'kautuka-base');
  const componentsById = new Map(components.map((component) => [component.id, component]));

  for (const image of sidecarImages) {
    const component = componentsById.get(image?.id);
    if (!component || component.image !== image.image) {
      throw new Error(`Third-party catalog is missing or stale for runtime image ${image?.id}; update compliance/third-party-components.json with verified source/license evidence.`);
    }
  }

  if (sidecarImages.length !== 2 || !componentsById.has('openobserve') || !componentsById.has('git-pkgs-proxy')) {
    throw new Error('Runtime image inventory changed; review and update the third-party component catalog.');
  }

  if (selectedBaseImage && !baseImagePattern.test(selectedBaseImage)) {
    throw new Error(`Release base image must be an immutable ghcr.io/devkrity/kautuka-base digest, got: ${selectedBaseImage}`);
  }
  if (baseImageEntry && baseImageEntry.image !== selectedBaseImage) {
    throw new Error('Pinned runtime kautuka-base image does not match the explicit --base-image release input.');
  }

  return sidecarImages;
}
