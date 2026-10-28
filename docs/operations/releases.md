# Release operations

Kautuka has two release channels. Both release from commits reachable from `main` only. The scheduled Preview job is disabled by default in a new repository; after cutover it can run nightly at 02:00 Brisbane time (`0 16 * * *` UTC) and skips when `main` has no commits since the last Preview tag. Stable releases require a manual run and approval. Each channel uses its own versioned GitHub Release and its own public pointer repository.

**Cutover status:** The release workflow is prepared, but no GitHub release or channel pointer has been published or verified end to end. Follow these procedures after the release feed repositories, environments, tokens, and release immutability are configured and validated.

## Scheduled release gate

The `resolve-channel` job runs for `workflow_dispatch` so operators can perform a manual dry run. Scheduled releases stay disabled unless the repository variable `KAUTUKA_RELEASES_ENABLED` is exactly `true`; an unset or any other value skips channel resolution before it can create a tag. Leave the variable unset or false in a fresh repository. Set it to `true` only after the release feeds and channel-scoped tokens, `preview` and `stable` environments, release immutability, signing/attestation, and live verification gates below are configured and checked. Manual Stable publication remains subject to approval by the `stable` environment. Manual Preview dispatches remain dry-run only.

## Nightly Preview

Set `KAUTUKA_UPCOMING_STABLE_VERSION` in the release workflow to the next plain SemVer, such as `0.1.20`. The nightly run computes the next `v0.1.20-preview.N`, tags the current `main` commit, builds the artifacts, creates a draft, uploads and validates the complete asset set, publishes the immutable GitHub prerelease, then updates the Preview pointer in `devkrity/kautuka-release-feed-preview`. Preview tags use `vX.Y.Z-preview.N`; versions use `X.Y.Z-preview.N`.

After Preview publication, install it from the Preview feed and set the app update channel to **Preview**. Check the displayed version and short commit SHA, exercise updates, and report regressions against the tagged commit. Preview Windows installers may be unsigned; check `SIGNING-STATUS.txt` in the release assets and expect a SmartScreen warning when it says `UNSIGNED`. A manual `workflow_dispatch` run can rebuild and verify an existing Preview tag with `dry_run=true`; manual dispatch does not publish Preview releases.

## Manual Stable release

1. Choose the Preview tag that passed testing, for example `v0.1.20-preview.3`, and record its commit.
2. Create the plain stable tag `v0.1.20` on that exact same commit. The workflow rejects a stable tag without a matching Preview tag on the same commit.
3. Manually dispatch **Release Pipeline** from `main` with the stable tag and `dry_run=true` to build and verify. Review the version, source SHA, artifacts, signatures, checksums, and CI results.
4. Dispatch again with `dry_run=false`. The `stable` GitHub Environment requires Amit's approval. After approval, the workflow creates a draft GitHub release, uploads and validates the complete flat asset set, publishes the immutable release, then updates the Stable pointer in `devkrity/kautuka-release-feed-stable`.

Stable versions are plain SemVer (`vX.Y.Z` tags, `X.Y.Z` product versions). Never reuse a published version or move a release tag to different source. To fix a release, cut a new version.

## Roll back

Revert the Stable pointer repository's manifest file in a reviewed commit to stop fresh installs and new offers from selecting the bad release. Already-installed clients reject a lower version as an update; use the appliance's explicit rollback path where available or publish a higher-version fix-forward release. Keep the newer release assets, tag, and pointer commit history for auditability. Never reuse a version number.

## Environment secrets

Release secrets belong in GitHub Environments, never repository-level secrets:

- `preview`: `release_manifest_private_key`, a fine-grained `release_feed_token` scoped only to `devkrity/kautuka-release-feed-preview`, and the S3 catalog credentials `release_feed_s3_endpoint`, `release_feed_s3_bucket`, `release_feed_s3_access_key_id`, `release_feed_s3_secret_access_key`, `release_feed_s3_region`, and `release_feed_s3_prefix`.
- `stable`: the same manifest signing and catalog credentials, a separate fine-grained `release_feed_token` scoped only to `devkrity/kautuka-release-feed-stable`, plus `WINDOWS_SIGNING_CERT_BASE64`, `WINDOWS_SIGNING_CERT_PASSWORD`, and optionally `WINDOWS_SIGNING_TIMESTAMP_URL`.

Create both pointer repositories as **private** with a `main` branch during preparation. Their only mutable file is `manifest`; each environment token must have Contents write access to its one repository and no access to the other channel. Each publish/build job selects the channel-named environment. Configure `stable` with Amit as required reviewer and restrict both environments to `main`. Unauthenticated clients cannot read private pointer repositories, so release cutover remains blocked until the repository owner explicitly makes the feed repositories public. This preparation does not change repository visibility.

Before the first release, enable **release immutability** in the `devkrity/kautuka` repository settings. GitHub applies this setting to future releases; the workflow creates each release as a draft, uploads and validates every asset, publishes it, checks GitHub reports the release immutable, and only then moves the channel pointer. Do not cut over until this repository setting and both pointer repositories/tokens are configured. The S3 step now publishes only the catalog; a catalog-host failure is reported separately and does not block a versioned release or its pointer.

The raw GitHub pointer is untrusted discovery data and may be cached briefly. Clients verify the detached signature with the key embedded by the installer, bind the signed version/channel/tag and exact-tag artifact URLs to the candidate, and verify artifact SHA-256 and size before installation. Pointer changes remain auditable as commits in the channel-specific repository. Fix releases forward; rollbacks move only the pointer.

### Linux installer verification before sudo

Download `kautuka-install-linux.sh` from the versioned GitHub Release, then verify its GitHub Actions provenance from an independently trusted GitHub identity before executing it as root:

```bash
gh attestation verify ./kautuka-install-linux.sh \
  --repo devkrity/kautuka \
  --signer-workflow devkrity/kautuka/.github/workflows/release-build-platform-assets.yml \
  && sudo bash ./kautuka-install-linux.sh
```

The command verifies the downloaded file's digest against GitHub's Sigstore-backed attestation and restricts the signer to this repository's platform asset workflow. The `&&` ensures the installer cannot run with `sudo` if verification fails. `bash scripts/install-verified-linux.sh ./kautuka-install-linux.sh` provides the same check-and-run sequence from a trusted source checkout. A mock-verifier regression checks that a substituted file is rejected before sudo execution; before cutover, verify a real published installer and a tampered copy against GitHub's live attestation service. Network installs also verify the exact versioned platform manifest and archive digest before creating accounts, writing configuration, or changing services.

## Enabling release branches later

`release/*` is intentionally disabled. To enable it later, obtain team approval, add `origin/release/*` to the release workflow's single allowed-branch list, and update both GitHub Environment deployment-branch policies to include the chosen `release/*` pattern. Keep `main` in the list and ensure stable promotion rules still require a tested Preview tag on the same commit.

## Build identity and verification

The release tag resolves the version, channel, tag, and source commit. CI stamps .NET, frontend, installers, container tags, manifests, and artifacts from that identity. `Directory.Build.props` contains only `KautukaDevVersion` for unpublished local `-dev` builds. Before publication the workflow checks the signed manifest and checksums, component identity, artifact names, and channel destination. The About screen, diagnostics, and startup logs show the full version and source SHA; installer and download names show the product version.

See the [security model](../architecture/security.md#release-trust) and the [release pipeline](../../.github/workflows/release-pipeline.yml).
