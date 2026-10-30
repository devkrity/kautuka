# Release operations

Stable is the supported release baseline; Preview is best-effort for evaluation. See the [support policy](../../SUPPORT.md) and [maintainer process](maintainers.md).

Kautuka publishes versioned GitHub Releases. Stable uses exact `vX.Y.Z` tags and GitHub's non-prerelease status; Preview uses exact `vX.Y.Z-preview.N` tags and prerelease status. The updater and both installers discover candidates from the canonical repository's anonymous Releases API, scan pages in bounded batches, and select the highest strict SemVer independently of publication order. Invalid, draft, unpublished, or wrong-channel entries are skipped. Duplicate candidates at the same parsed version fail closed. Discovery stops after the first short page; exactly 1,000 releases are supported with an empty sentinel page, while more than 1,000 fails closed. The 1,000-release ceiling bounds discovery to at most eleven anonymous API requests; ordinary release histories usually fit in one request. Network, rate-limit, or malformed-metadata failures stop discovery. A signed manifest fetched from the selected exact tag must match its selected tag, version, channel, source commit, signature URL, artifact URL, digest, and size.

**Cutover status:** Code changes alone do not prove a live release/install path. Keep `KAUTUKA_RELEASES_ENABLED` unset or false until the repository is public and live release, signing, attestation, and install/update checks pass. Clients require anonymous access to Releases, so private repository releases cannot be consumed by ordinary installs.

## Scheduled release gate

The `resolve-channel` job runs for `workflow_dispatch` so maintainers can perform a manual dry run. Scheduled releases stay disabled unless the repository variable `KAUTUKA_RELEASES_ENABLED` is exactly `true`; an unset or other value skips channel resolution before tag creation. Set it to `true` only after the source repository is public, release immutability is configured, signing and attestation work, and a live signed install/update rehearsal succeeds. Before Stable publication, verify the `stable` GitHub Environment requires owner approval.

## Nightly Preview

Set `KAUTUKA_UPCOMING_STABLE_VERSION` to the next plain SemVer, such as `0.1.20`. The nightly run computes the next `v0.1.20-preview.N`, tags the current `main` commit, builds the artifacts, creates a draft, uploads and validates the complete asset set, and publishes an immutable GitHub prerelease. If no commits have landed since the latest Preview tag, a retry can reuse that exact tag only when no release exists or a matching empty draft exists. A partial draft or mismatched release identity stops for maintainer review. A matching published release is skipped only when GitHub reports it immutable. The workflow never moves or deletes tags and does not replace published assets.

For an immediate Preview, dispatch **Release Pipeline** from `main`, set `publish_preview_now=true`, leave `release_tag` empty, and set `dry_run=false`. This explicit opt-in uses the same next-tag selection, immutable-release retry checks, signing, attestation, artifact validation, draft upload, and publish path as the scheduled run. Without that opt-in, manual Preview dispatches can only rebuild and verify an existing Preview tag with `dry_run=true`.

After Preview publication, install it from the public repository's Releases page and set the app update channel to **Preview**. Check the displayed version and short commit SHA, exercise updates, and report regressions against the tagged commit. Preview Windows installers may be unsigned; Authenticode status is disclosed in `SIGNING-STATUS.txt`.

## Manual Stable release

1. Choose the Preview tag that passed testing, for example `v0.1.20-preview.3`, and record its commit.
2. Create the plain stable tag `v0.1.20` on that same commit. The workflow rejects a stable tag without a matching Preview tag on the same commit.
3. Dispatch **Release Pipeline** from `main` with the stable tag and `dry_run=true`. Review the version, source SHA, artifacts, signatures, checksums, and CI results.
4. Dispatch with `dry_run=false` only after the `stable` GitHub Environment enforces owner approval. The workflow creates a draft GitHub Release, uploads and validates the flat asset set, publishes the immutable release, and checks GitHub's immutable status.

Stable tags are plain SemVer (`vX.Y.Z`; product version `X.Y.Z`). Do not reuse published versions or move a release tag to different source. Fix a bad release forward with a new version.

## Release recovery

Releases are immutable and tags are never moved or deleted by the workflow. An installed client rejects a release at or below its installed version. Reverting discovery state is not part of this design; removing an offer requires a higher-version fix-forward, while an installed appliance may use the explicit last-known-good rollback when available. Exact-tag downloads remain available for a published release. A first install has no installed-version high-water mark, so signed release identity and exact-tag URLs are important even before updates are enabled.

If publication fails before the release is published, inspect the exact release tag. The workflow can retry an absent release or a matching empty draft; a partial draft requires maintainer review. The updater discovers releases from GitHub Releases; catalog hosting is a separate service and is not part of release discovery or the release publisher.

## Environment secrets and repository settings

Release secrets belong in GitHub Environments, never repository-level secrets:

- `preview`: `release_manifest_private_key`.
- `stable`: `release_manifest_private_key`. Authenticode signing is performed when the Windows signing secrets are configured; otherwise `SIGNING-STATUS.txt` discloses that the installer is unsigned.

The `preview` and `stable` environments apply to the canonical source repository's release workflows. Restrict deployments to the intended `main` branch; configure Stable to require owner approval before enabling publication. The source repository must have GitHub release immutability enabled. The workflow creates releases as drafts, validates the full asset set, publishes them, and checks the resulting `.immutable` field. The `GET /immutable-releases` readback requires `Administration: read`, which the contents-only workflow token does not have.

The release workflow publishes versioned release assets to GitHub Releases only. It does not generate or upload catalog data, and installers do not carry catalog settings. The Update Service configures catalog discovery at runtime for the in-app connector and skill directory. No Cloudflare, R2, or S3 publishing is part of the release workflow.
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
