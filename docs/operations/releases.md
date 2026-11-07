# Release operations

Stable is the supported release baseline; Preview is best-effort for evaluation. See the [support policy](../../SUPPORT.md) and [maintainer process](maintainers.md).

Kautuka publishes versioned GitHub Releases. Stable uses exact `vX.Y.Z` tags and GitHub's non-prerelease status; Preview uses exact `vX.Y.Z-preview.N` tags and prerelease status. The updater and both installers discover candidates from the canonical repository's anonymous Releases API, scan pages in bounded batches, and select the highest strict SemVer independently of publication order. Invalid, draft, unpublished, or wrong-channel entries are skipped. Duplicate candidates at the same parsed version fail closed. Discovery stops after the first short page; exactly 1,000 releases are supported with an empty sentinel page, while more than 1,000 fails closed. The 1,000-release ceiling bounds discovery to at most eleven anonymous API requests; ordinary release histories usually fit in one request. Network, rate-limit, or malformed-metadata failures stop discovery. A signed manifest fetched from the selected exact tag must match its selected tag, version, channel, source commit, signature URL, artifact URL, digest, and size.

The Gateway's production configuration contains the public Google and Microsoft sign-in client IDs used to validate identity-token audiences. These IDs must match the login applications configured in the hosted authentication broker; the corresponding client secrets stay with the broker. If a login application is rotated, update and verify the Gateway configuration as well.

The broker-connection-controls release changes the hosted authentication protocol. Coordinate the broker and Gateway releases: older Gateway versions cannot start or renew hosted sign-ins after the old broker routes are retired. Existing hosted mailbox connections require reconnection; users can remove a previous grant through their provider's permissions page. Existing Kautuka app sessions are not automatically signed out by this protocol change. Verify both Google and Microsoft social sign-in and mailbox connections against staging before rollout. See [Authentication and mail broker](authentication-broker.md) for the flow, configuration and recovery guidance.

Before publishing that broker change, provision its separate `MAIL_ACCOUNT_PSEUDONYM_KEY` as a protected, backed-up 32-byte key and verify the configured Durable Object migrations and rate-limit bindings. The broker fails closed for mailbox sign-in without this key. Replacing it without a coordinated migration changes account identifiers and breaks allowance accounting. Follow the broker repository's deployment instructions; do not put the key in source control or logs.

Kautuka-maintained container image definitions are organized under `src/images/`. The sandbox runtime image `ghcr.io/devkrity/kautuka-base` has its own lifecycle, separate from application releases. A maintainer dispatches **Release Kautuka Base Image** with a new image version to build and test the base, then publish that independent version tag to GHCR. To adopt a new base image, update `src/images/kautuka-base/release.json` in a reviewed Kautuka change with the exact digest reported by the image release workflow. The Kautuka app release workflow pulls that digest and includes it in the signed runtime image allowlist; it never builds or pushes the base image. Before the first app release, publish the initial base image and set its GHCR package visibility to public, then verify anonymous access to the exact `ghcr.io/devkrity/kautuka-base@sha256:…` digest. The versioned GHCR tag is for release identification; app releases consume only the reviewed digest.

The OpenObserve and package-proxy images are mirrored from their pinned upstream digests into `ghcr.io/devkrity/kautuka-observability` and `ghcr.io/devkrity/kautuka-package-proxy`. **Mirror Runtime Image** copies every platform from the allowlisted upstream digest and refuses to overwrite an existing Devkrity version tag. Its result is selected by reviewed digest files under `src/images/`, and the app runtime image catalog and package-proxy wrapper consume those same digests. Publish a new mirror version only after deliberate review; never configure runtime consumers with floating tags. Make both GHCR packages public before anonymous installs, and verify pulls by digest. The release compliance assets continue to identify upstream source, license, and source commit for each mirrored image.

**Cutover status:** Code changes alone do not prove a live release/install path. Keep `KAUTUKA_RELEASES_ENABLED` unset or false until the repository is public and live release, signing, attestation, and install/update checks pass. Clients require anonymous access to Releases, so private repository releases cannot be consumed by ordinary installs.

## Scheduled release gate

The `resolve-channel` job runs for `workflow_dispatch` so maintainers can perform a manual dry run. Scheduled releases stay disabled unless the repository variable `KAUTUKA_RELEASES_ENABLED` is exactly `true`; an unset or other value skips channel resolution before tag creation. Set it to `true` only after the source repository is public, release immutability is configured, signing and attestation work, and a live signed install/update rehearsal succeeds. Before Stable publication, verify the `stable` GitHub Environment requires owner approval.

## Nightly Preview

Set `KAUTUKA_UPCOMING_STABLE_VERSION` to the next plain SemVer, such as `0.1.21`. The scheduled run computes the next `v0.1.21-preview.N`, tags the current `main` commit, builds the artifacts, creates a draft, uploads and validates the complete asset set, and publishes an immutable GitHub prerelease. If no commits have landed since the latest Preview tag, a retry can reuse that exact tag only when no release exists or a matching empty draft exists. A partial draft or mismatched release identity stops for maintainer review. A matching published release is skipped only when GitHub reports it immutable. The workflow never moves or deletes tags and does not replace published assets.

For an immediate Preview, dispatch **Release Pipeline** from `main`, set `publish_preview_now=true`, leave `release_tag` empty, and set `dry_run=false`. This explicit opt-in uses the same next-tag selection, immutable-release retry checks, signing, attestation, artifact validation, draft upload, and publish path as the scheduled run. Without that opt-in, manual Preview dispatches can only rebuild and verify an existing Preview tag with `dry_run=true`.

## Release stabilization

Contributors target `main`. When a release needs independent stabilization, create `release/vX.Y.Z` from the selected `main` commit and protect it with the same PR and CI requirements as `main`. Keep normal PRs flowing to `main`; cherry-pick only reviewed release blockers, with tests, into the release branch. Ensure each fix also exists on `main`. Temporarily disable scheduled Preview publication while preparing the branch and use manual dispatch with `source_branch=release/vX.Y.Z` and `publish_preview_now=true` to publish each candidate. The latest Preview tag, even from a previous version, must be an ancestor of the selected source branch; a higher version cannot send Preview users to divergent code and potentially incompatible databases. After Stable publication, merge the release branch into `main` through a reviewed PR to preserve its commit ancestry, bump `KAUTUKA_UPCOMING_STABLE_VERSION` to the next version, and only then re-enable scheduled Preview publication. Do not delete the release branch until publication and the merge back complete; delete it afterward, retaining immutable tags.

For an urgent patch after a newer Preview has diverged, do not publish the patch branch as public Preview. The current pipeline deliberately rejects that unsafe transition; use an isolated candidate process before enabling such patch releases. A versioned patch branch alone is not sufficient authorization to move Preview users backward in source history.

Record the candidate tag, commit, installed source version, platform, and test results. Test the exact candidate's installed upgrade, failed activation and recovery, and the Stable artifacts from that same commit. A published Preview tag establishes identity, not proof of testing. Check database compatibility as well as version ordering: Preview users who select Stable must wait for a newer compatible Stable; never force an older or incompatible Stable over Preview data.

After Preview publication, install it from the public repository's Releases page and set the app update channel to **Preview**. Check the displayed version and short commit SHA, exercise updates, and report regressions against the tagged commit. Preview Windows installers may be unsigned; Authenticode status is disclosed in `SIGNING-STATUS.txt`.

The update channel shown in advanced settings reflects UpdateService's effective policy. If UpdateService is temporarily unreachable or its client certificates are unavailable during startup, the page falls back to the last saved Gateway setting; check service health before changing channels in that case.

## Manual Stable release

1. Choose the Preview tag that passed testing, for example `v0.1.20-preview.3`, and record its commit.
2. Create the plain stable tag `v0.1.20` on that same commit. The workflow rejects a stable tag without a matching published Preview release on the same commit.
3. Dispatch **Release Pipeline** from `main` with the stable tag, the source branch (`main` or `release/v0.1.20`), and `dry_run=true`. Review the version, source SHA, artifacts, signatures, checksums, and CI results.
4. Dispatch with `dry_run=false` only after the `stable` GitHub Environment enforces owner approval. The workflow creates a draft GitHub Release, uploads and validates the flat asset set, publishes the immutable release, and checks GitHub's immutable status.

Stable tags are plain SemVer (`vX.Y.Z`; product version `X.Y.Z`). Do not reuse published versions or move a release tag to different source. Fix a bad release forward with a new version.

Both channel environments must use the common release signing key whose public key was provisioned by Preview 22. The build fails before publishing if any platform derives a different public-key fingerprint. Stable `0.1.20` was published with an older distinct key; its assets remain immutable, and it is not a supported automatic key transition to the common-key release. Do not expose signing private keys in logs or repository files.

## Release recovery

Releases are immutable and tags are never moved or deleted by the workflow. An installed client rejects a release at or below its installed version. The appliance has no user-facing release rollback path: committed problems are repaired by a higher signed release. Exact-tag downloads remain available for a published release. A first install has no installed-version high-water mark, so signed release identity and exact-tag URLs are important even before updates are enabled.

If publication fails before the release is published, inspect the exact release tag. The workflow can retry an absent release or a matching empty draft; a partial draft requires maintainer review. The updater discovers releases from GitHub Releases; catalog hosting is a separate service and is not part of release discovery or the release publisher.

Linux update recovery is limited to a pre-commit failure. The root helper first writes a durable maintenance fence, stops and verifies the database-writing host services, then captures a SQLite-consistent snapshot of the supported databases and their matching authentication and certificate keys. `PRAGMA integrity_check`, operation identity, data-root identity, and file hashes are checked before the snapshot can be used. Gateway, Manager, CredentialsHost, ChannelHost, UpdateService, and WebHost remain fenced from work while target services are checked. Health endpoints and the updater's recovery probes remain available.

If target activation or readiness fails before commit, the helper restores the exact source pointers, databases, and keys from that operation's verified snapshot. The boot recovery unit runs before `user@` and application services; it restores files and pointers without waiting for the service-user bus or starting application units. It then records a durable `recovered` decision, so subsequent recovery attempts never replay the snapshot over later writes. The source UpdateService checks service health and desired-state regeneration before it records the terminal `completed` decision and removes the maintenance fence. This process is idempotent across crashes and reboots. The last completed verified database snapshot remains available while a candidate is validated; a committed update retains its pre-update snapshot as the single last-known-good database backup. No container or image data, workspace files, attachments, or other file stores are copied or restored. After commit, Kautuka never automatically switches back or restores databases; repair forward with a newer signed release.

The automatic snapshot protocol requires the current Linux installer bootstrap. UpdateService checks that the installed helper advertises `safe-state-recovery-v5`; a legacy helper, unresolved legacy recovery marker, custom data/socket roots, or unsupported service layout is rejected before release pointers change. Run the current signed Linux installer as root to migrate a legacy installation before using in-app updates. Do not manually remove `/var/lib/kautuka-recovery/maintenance.json`; an unreadable journal or invalid snapshot intentionally leaves the appliance fenced for internal administrator recovery.

WebHost remains running during the database snapshot and core service restart, serving a static update page while admission is fenced. Once the selected Gateway and Manager are ready, the helper switches WebHost to the selected release; that process restart can briefly interrupt connections. The signed package declares this capability with its WebHost maintenance marker, and an older installation must be bootstrapped before using this update protocol.

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

The privileged apply helper independently verifies the signed manifest and an archive copied into a root-only temporary directory. The manifest verification key and `/etc/kautuka` directory must be root-owned and not group or world writable. Installations created by older installers may have a service-owned key or an older helper; migrate them using a verified current installer run as root before relying on these protections. A helper that requires signed metadata cannot apply an artifact staged by an older updater that did not retain the manifest and signature; update both components through the installer during migration.

The Windows appliance rootfs must include `openssl`, `python3`, and `base64` for privileged verification. Refresh and promote the pinned WSL rootfs image before releasing this change; older images can lack `openssl`. The release builder rejects a rootfs missing these executables.

Before switching release pointers, the root helper writes an operation-bound journal and durable maintenance fence under `/var/lib/kautuka-recovery`, drains and verifies the Gateway, ChannelHost, CredentialsHost, Manager, and WebHost processes, then captures and checks the supported SQLite databases and matching authentication material. If target health fails before commit, it restores the verified snapshot, restores the recorded source pointers, and keeps admission fenced until the source release and its current UpdateService are healthy. The boot recovery unit repeats incomplete pre-commit recovery safely. Once the operation is committed, recovery proceeds forward and never restores the old database snapshot. The snapshot excludes containers, images, workspace files, attachments, and other file stores. Older helpers fail the recovery capability handshake; run the current signed Linux installer bootstrap before applying an update from those installations. Recovery internals are for appliance administration and are not exposed as a user rollback control.

After a committed update, built-in connector bootstrap waits until the maintenance fence is removed before contacting Manager. During a user-initiated update, the initiating browser shows restart progress instead of a transient updater socket error for up to 12 minutes; unrelated failures and an outage that outlasts that window remain visible.

For container deployments, the root apply helper stages the eleven offline image
roles before installing the target Manager. Archive names include their signed
SHA-256 and existing files must match that digest. The shared cache is
`/var/lib/kautuka/oci-images`, root:kautuka mode 0750 with root:kautuka mode 0640
files under root-owned, non-writable ancestry. The helper flushes new files and
directory entries and retains source-release archives. It rejects a legacy
service-writable data parent; installer conversion must establish this layout
before enabling container mode. Archive validation does not replace release
signature verification or runtime config-ID/platform checks. Staging-space checks
include the additional immutable image cache on the data filesystem and
format-3 Gateway/agent databases when sizing recovery snapshots.

The Fedora update transaction regression covers archive retention across target
staging, invalid cohort recovery and interrupted-update source recovery. It uses
signed fixture archives and mocked lifecycle/systemd calls; actual installed
container handoff remains a separate release gate.

Container deployment settings live in the installer-owned
`/etc/kautuka/manager.json`, shared by ordinary Manager startup and its fixed
update-maintenance command. On Linux this file overrides release defaults and
normal environment/argument configuration, and is read once without live reload.
It is optional for legacy host installations; the installer must publish and
validate container enablement, DNS and core settings before enabling the
container service contract. Both startup paths reject a container contract with
a missing installed file or absent/disabled core or egress flags; earlier
configuration providers cannot stand in for those explicit installed settings.
Updates preserve it instead of copying deployment
settings into each release. The maintenance command still forces the production
mode/state root and adds no environment or argument configuration providers.

## Build identity and verification

The release tag resolves version, channel, tag, and source commit. CI stamps .NET, frontend, installers, container tags, manifests, and artifacts from that identity. `Directory.Build.props` contains only `KautukaDevVersion` for unpublished local `-dev` builds. Before publication the workflow checks the signed manifest and checksums, component identity, asset names, and channel tag format. The About screen, diagnostics, and startup logs show the full version and source SHA; installer and download names show the product version.

See the [security model](../architecture/security.md#release-trust) and the [release pipeline](../../.github/workflows/release-pipeline.yml).
