# Release operations

Stable is the supported release baseline; Preview is best-effort for evaluation. See the [support policy](../../SUPPORT.md) and [maintainer process](maintainers.md).

Kautuka uses versioned GitHub Releases for artifacts and a `release-channel` branch in `devkrity/kautuka` for channel discovery. `stable.json` and `preview.json` are byte-for-byte copies of their already-signed release manifest. Stable accepts exact `vX.Y.Z` tags; Preview accepts exact `vX.Y.Z-preview.N` tags. The publishing workflow advances only the selected file, only after GitHub reports the release immutable, and only to a strictly higher version. Clients make one anonymous raw-content request for the selected channel; they do not scan release history or depend on API ordering. The signed manifest binds version, channel, release tag, source commit, signature URL, artifact URL, digest, and size. Clients verify that identity and signature before offering or applying an update; installers verify the archive digest before system mutation.

**Cutover status:** The release and discovery code is prepared locally. No public release or channel snapshot has been published or verified end to end. Clients need unauthenticated read access to the canonical repository's `release-channel` branch and its GitHub Releases, so the owner must make `devkrity/kautuka` public before cutover. Keep `KAUTUKA_RELEASES_ENABLED` unset or false until the repository is public and live release, signing, attestation, and install/update checks pass.

## Scheduled release gate

The `resolve-channel` job runs for `workflow_dispatch` so maintainers can perform a manual dry run. Scheduled releases stay disabled unless the repository variable `KAUTUKA_RELEASES_ENABLED` is exactly `true`; an unset or other value skips channel resolution before tag creation. Set it to `true` only after the source repository is public, the `release-channel` branch and release immutability are configured, signing and attestation work, and a live signed install/update rehearsal succeeds. Before Stable publication, verify the `stable` GitHub Environment requires owner approval. Manual Preview dispatches remain dry-run only.

## Nightly Preview

Set `KAUTUKA_UPCOMING_STABLE_VERSION` to the next plain SemVer, such as `0.1.20`. The nightly run computes the next `v0.1.20-preview.N`, tags the current `main` commit, builds the artifacts, creates a draft, uploads and validates the complete asset set, and publishes an immutable GitHub prerelease. After immutable publication it verifies the candidate and current signed manifest, requires a strictly higher Preview version, then advances `preview.json` only. If no commits have landed since the latest Preview tag, a retry can reuse that exact tag only when no release exists or a matching empty draft exists. A partial draft or mismatched release identity stops for maintainer review. A matching published release is skipped only when GitHub reports it immutable. The workflow never moves or deletes tags and does not replace published assets.

After Preview publication, install it from the public repository's Releases page and set the app update channel to **Preview**. Check the displayed version and short commit SHA, exercise updates, and report regressions against the tagged commit. Preview Windows installers may be unsigned; Stable installers may also be unsigned when signing secrets are not configured. Check `SIGNING-STATUS.txt` in the release assets and expect a SmartScreen warning when it says `UNSIGNED`. Manual `workflow_dispatch` can rebuild and verify an existing Preview tag with `dry_run=true`; it does not publish Preview releases.

## Manual Stable release

1. Choose the Preview tag that passed testing, for example `v0.1.20-preview.3`, and record its commit.
2. Create the plain stable tag `v0.1.20` on that same commit. The workflow rejects a stable tag without a matching Preview tag on the same commit.
3. Dispatch **Release Pipeline** from `main` with the stable tag and `dry_run=true`. Review the version, source SHA, artifacts, signatures, checksums, and CI results.
4. Dispatch with `dry_run=false` only after the `stable` GitHub Environment enforces owner approval. The workflow creates a draft GitHub Release, uploads and validates the flat asset set, publishes the immutable release, checks GitHub's immutable status, verifies the signed candidate/current manifests and advances `stable.json` only to a strictly higher Stable version.

Stable tags are plain SemVer (`vX.Y.Z`; product version `X.Y.Z`). Do not reuse published versions or move a release tag to different source. Fix a bad release forward with a new version.

## Release recovery

The `release-channel` branch is the discovery pointer. Reverting a channel file stops new discovery from offering the reverted release, but does not withdraw immutable exact-tag downloads or invalidate offers already cached by clients. The publisher rejects lower versions, so restoring an earlier signed pointer is a privileged manual rollback and is intentionally unavailable through the normal pipeline. For installed appliances, use the explicit last-known-good rollback when available. For a release-wide regression, publish a higher-version fix-forward release after validation. Do not move or delete a published tag or replace release assets. A first install has no installed-version high-water mark; a privileged branch writer can replay an older valid signed pointer, so protect branch updates and audit its commit history.

If publication fails before the release is published, inspect the exact release tag. The workflow can retry an absent release or a matching empty draft; a partial draft requires maintainer review. If publication succeeds but catalog upload fails, the GitHub Release remains available and the workflow reports the catalog failure separately. The catalog is hosted at `kautuka.store/catalog/manifest` and is not part of channel discovery.

## Environment secrets and repository settings

Release secrets belong in GitHub Environments, never repository-level secrets:

- `preview`: `release_manifest_private_key` and the existing S3 catalog credentials (`release_feed_s3_endpoint`, `release_feed_s3_bucket`, `release_feed_s3_access_key_id`, `release_feed_s3_secret_access_key`, `release_feed_s3_region`, and `release_feed_s3_prefix`).
- `stable`: the same manifest signing and catalog credentials. Authenticode signing is performed when `WINDOWS_SIGNING_CERT_BASE64` and `WINDOWS_SIGNING_CERT_PASSWORD` are configured for the release job; `WINDOWS_SIGNING_TIMESTAMP_URL` is optional. Without those signing secrets the installer remains unsigned and its status file discloses that fact for either channel.

The `preview` and `stable` environments apply to the canonical source repository's release workflows. Restrict deployments to the intended `main` branch; configure Stable to require owner approval before enabling publication. The source repository must have GitHub release immutability enabled. An owner can set it with `PUT /repos/{owner}/{repo}/immutable-releases` and verify with `GET`; the GET requires `Administration: read`, which the contents-only workflow token does not have. The workflow creates releases as drafts, validates the full asset set, publishes them, and checks the resulting `.immutable` field.

The release workflow continues to publish only the catalog to the existing S3 host. A catalog-host failure is reported separately and does not block the versioned GitHub Release or channel snapshot.

## Channel branch setup and trust boundary

Before enabling release publication, initialize a `release-channel` branch with a small `README.md` explaining the two channel files. The pointer files are absent before the first release; clients fail closed until the first signed snapshot is published. Protect this branch from deletion and force-push. Configure a separate update-restriction ruleset that permits the GitHub Actions application used by the workflow; the pointer checkout explicitly uses the workflow's `GITHUB_TOKEN` and persists its credentials for a normal fast-forward push. Keep the no-delete/no-force-push rules in a separate ruleset with no bypass actor, because a bypass actor can bypass every rule in its ruleset. The publisher token has repository-level contents write access, not path-scoped access. The workflow stages only the selected channel file and validates its signed manifest, but a compromised job/token or privileged repository writer could change either file. Review and audit every pointer commit. If the signing key is rotated, update the trusted key through a separately reviewed transition; the publisher intentionally fails closed if it cannot verify the current pointer with the active key.

The channel files are untrusted discovery input. A valid signature and exact-tag URL checks prevent an attacker from substituting a forged release manifest or artifact. They cannot prevent replay by a branch administrator of an older, still-valid signed snapshot. Installed clients reject a version at or below their installed version; a first installation has no prior version to compare. Treat branch protection, publisher workflow review, and commit audit as part of the release trust boundary.

If a release is immutable but pointer publication fails, the pipeline does not automatically repair the pointer. Verify the release's immutable status and signed manifest identity, then rerun the reviewed publisher workflow for that exact candidate after checking that it is still newer than the current channel file. Do not move the tag or replace release assets.

### Linux installer verification before sudo

Download `kautuka-install-linux.sh` from a versioned GitHub Release, then verify its GitHub Actions provenance from an independently trusted GitHub identity before executing it as root:

```bash
gh attestation verify ./kautuka-install-linux.sh \
  --repo devkrity/kautuka \
  --signer-workflow devkrity/kautuka/.github/workflows/release-build-platform-assets.yml \
  && sudo bash ./kautuka-install-linux.sh
```

The `&&` ensures the installer cannot run with `sudo` if verification fails. This checks the downloaded file against GitHub's Sigstore-backed attestation and restricts the signer to this repository's platform asset workflow. Before cutover, verify a real published installer and a tampered copy against GitHub's live attestation service. Network installs anonymously discover the selected channel, then verify the exact-tag manifest signature and archive digest before creating accounts, writing configuration, or changing services.

## Build identity and verification

The release tag resolves version, channel, tag, and source commit. CI stamps .NET, frontend, installers, container tags, manifests, and artifacts from that identity. `Directory.Build.props` contains only `KautukaDevVersion` for unpublished local `-dev` builds. Before publication the workflow checks the signed manifest and checksums, component identity, asset names, and channel tag format. The About screen, diagnostics, and startup logs show the full version and source SHA; installer and download names show the product version.

See the [security model](../architecture/security.md#release-trust) and the [release pipeline](../../.github/workflows/release-pipeline.yml).
