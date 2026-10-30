const baseImagePattern = /^ghcr\.io\/devkrity\/kautuka-base@sha256:[a-f0-9]{64}$/u;

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
