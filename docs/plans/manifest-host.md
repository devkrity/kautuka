# Signed release manifest publication

**Status:** Active

**Owner:** Release engineering

**Activation condition:** A maintainer-approved stable release is built, its referenced artifacts pass validation, and the release pipeline publishes the signed manifest.

## Outcome

Publish independently verifiable update metadata and provision official installers with the expected update source and public verification key.

## Selected architecture

- GitHub Actions builds and publishes release artifacts through the maintained release pipeline.
- UpdateService verifies the manifest signature and artifact hashes before applying a release.
- Local release scripts provide a smoke-test lane; they are not a publication mechanism.

## Work

1. [x] Define separate update channels and signed manifest formats.
2. [x] Build and publish artifacts before making their manifests available.
3. [x] Provision official installers with the update source and trusted public key.
4. [x] Verify that UpdateService rejects invalid signatures and hashes.
5. [ ] Publish the stable `0.1.20` release only after maintainer approval and release checks.

The legacy beta manifest remains reachable but advertises an earlier prerelease. It is not the stable release source and must not be treated as a current Kautuka release. This plan records the remaining stable publication gate; no release publication is authorized by this document.
