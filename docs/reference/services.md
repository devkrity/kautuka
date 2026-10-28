# Service reference

This page summarizes deployable service behavior from entry points, registrations, RPC contracts, persistence code, and tests.

## Gateway

**Entry point:** `src/backend/Kautuka.Gateway/Program.cs`

Gateway is the public API and orchestration host. It activates `kautuka.db`, registers the runtime/recovery workers, maps HTTP endpoints, executes agents, persists canonical events, projects Narad, coordinates internal services, and exposes health/readiness. On startup it reimports built-in skill packages (`kautuka-docs`) from the Gateway content root into `/var/lib/kautuka/skills`. If the persisted package hash already matches, it skips the replace. A locked `SKILL.md` during that replace is logged and skipped so the host stays up; it must not abort the process. The approval-resolution dispatcher polls SQLite for staged HITL events; an `ObjectDisposedException` from `TryLeaseResolutionEvent` is logged and retried on the next tick so `BackgroundServiceExceptionBehavior=StopHost` cannot take down Gateway. Conversation file bytes are stored under Manager-resolved workspace paths; session asset metadata is listed and downloaded over `GET /api/sessions/{sessionId}/assets` (optional `ids`) and `GET /api/sessions/{sessionId}/assets/{assetId}`, after `POST` multipart upload. Narad carries only `attachmentIds` on user messages (including file-only turns and channel captions). Internal gRPC clients defer channel creation until the first call so Gateway can start before dependent certificates exist. A missing peer certificate is loaded again on the next call; Gateway does not remember that miss for the process lifetime. A shared execution-readiness service guards chat, channels, schedules, continuations, and agent-as-a-tool runs and supplies the authenticated UI status endpoint.

## Manager

**Entry point:** `src/backend/Kautuka.Manager/Program.cs`

**Runtime implementation:** `src/backend/Kautuka.Manager.RuntimeContainer`

Manager persists accepted desired state in `manager-desired-state.json`, reloads incomplete work, and reconciles Podman state on startup, wakeups, and drift intervals. It owns workspace path authority, sandbox creation, execution, connector containers, provenance, and orphan cleanup; Gateway currently performs attachment-file I/O inside Manager-resolved workspace paths. Built-in MCPB archives live under `/var/lib/kautuka/mcpb-bundles` as `2750` `kautuka-gateway:kautuka` with files `0640`, so Manager can read them to materialize connector images. Ledger writes use temporary files, write-through, and replacement. Accepted and reconciled cursors advance independently. Manager issues separate Gateway and UpdateService client certificates and enforces an exact per-method allowlist; UpdateService is limited to ping and host-status reads.

Host status keeps three signals separate. A successful status RPC proves Manager is reachable; a last-known Podman sample reports container-runtime availability; and accepted/reconciled cursors plus the execution gate report agent-workspace readiness. A Manager background loop runs the bounded live `podman info` check (5s when healthy, 2s after unavailable) and keeps that sample warm. Status, execution-readiness (chat and the UI banner), and reconcile read the snapshot instead of inducing `podman info`. Concurrent first callers share one in-flight check; a concrete process/connection failure is reported immediately, while a command timeout is confirmed once. Gateway evaluates the Podman failure before the Manager-ready response and publishes it as the `container_runtime` dependency.

## CredentialsHost

**Entry point:** `src/backend/Kautuka.CredentialsHost/Program.cs`

CredentialsHost initializes an isolated SQLite vault and exposes user/system credential operations plus merged runtime resolution. It enforces mTLS client identity and application capabilities. Mutations are transactional and idempotent; audit and rotation state are durable. Backup, restore, and key rotation can run as exclusive startup modes. Gateway treats unavailable, degraded, integrity-failed, expired-identity, and expiring-identity health as typed execution-readiness failures. On Linux, Gateway reads `credentialsHost/transport/.credentials-client.pfx` as a member of `kautuka-credentials-client`; that directory is `2750` with setgid on the client group so new PFX files stay group-readable. The vault stays `0700` on the CredentialsHost account.

## ChannelHost

**Entry point:** `src/backend/Kautuka.ChannelHost/Program.cs`

ChannelHost runs Telegram long polling and Slack Socket Mode adapters. It persists non-secret attachment settings, CredentialsHost references, provider cursors, and a normalized ingress outbox. Gateway supplies resolved secrets over a capability-authorized runtime RPC; plaintext secrets are never written to ChannelHost storage. Ingress remains pending until Gateway acknowledges successful processing, so disconnects and restarts replay safely by stable provider event ID.

The provider catalogue is admission-filtered and currently contains Telegram and Slack. Unknown provider keys fail with `provider_not_found`; there is no compatibility mapping for removed providers. CHG-019 for WhatsApp is deferred and remains unadmitted while Kautuka requires polling or an outbound-initiated persistent connection and permits no inbound webhooks. It may be revisited if an official polling-compatible transport becomes available or the product owner reopens the decision; no unofficial transport is authorized.

Provider provisioning performs provider-native readiness checks instead of treating saved credentials as sufficient. Telegram verifies the bot and publishes its command metadata. Slack validates both token classes, required bot permissions, workspace identity, and Socket Mode access; the generated manifest also installs the native Kautuka slash-command catalogue. Provisioning starts in `PROVISIONING`, becomes `ACTIVE` only after provider validation succeeds, and moves to `RECOVERY_REQUIRED` on failure. These operations return fixed actionable outcomes; provider response bodies and credentials are not exposed to the browser.

Gateway exposes owner-scoped self-service suspend and recover transitions. Suspension releases ChannelHost runtime credentials and stops new traffic without erasing the registration or audit trail. Recovery returns the registration to `INACTIVE`; saving replacement credentials and completing provider-native provisioning is required before activation. Terminal deletion remains administrative because current deterministic attachment IDs cannot recreate a deleted provider slot safely.

### Channel runtime ownership map

| Component | Authority and durable state | Inputs | Outputs and side effects | Prohibited responsibilities |
|---|---|---|---|---|
| Gateway runtime | Runs, waits, approval decisions, continuation claims, invocation lineage, canonical event ledger | Authenticated browser/channel commands and committed recovery work | Canonical runtime events and authorized execution | Provider protocol, consumer cursor, transport delivery truth |
| Runtime event publisher | Transactional event/outbox publication and root sequence | Validated runtime facts | Committed ledger rows, recoverable outbox delivery, live wake-up | Inventing missing events or repairing invalid history |
| Runtime projection reducer | Reconstructible run, message, tool, wait, and open-lifecycle view | Ordered canonical events | Immutable projection state | Mutations, approval resolution, continuation, provider delivery |
| Consumer attachment | Durable consumer cursor and snapshot/live handoff | Canonical history, live event tail, consumer identity | Gap-free deduplicated delivery | Deciding terminal state or writing compensating runtime events |
| Narad projector/writer | Standards-aligned browser wire representation | Approved projection state and canonical events | Narad authority events, hydrate records, session revisions | Alternate transcript or runtime state machine |
| Gateway channel ingress | Authenticated owner/session routing and command admission | Leased normalized ChannelHost ingress | Canonical user command; ACK only after admission completes | Trusting provider identity as Kautuka authority |
| Gateway channel projection | Semantic channel delivery intent | Canonical runtime events and owner routing | Authorized semantic outbound work | Provider formatting, direct runtime continuation |
| ChannelHost authorization | Workload, delegation, operation, owner, digest, replay, and contract enforcement | mTLS peer and signed RPC envelope | Allow/deny plus privacy-safe audit evidence | Trusting caller capabilities or provider identity |
| Provider adapter | Provider authentication, normalization, protocol cursor/session, native rendering | Resolved operation-scoped credentials and authorized semantic work | Durable normalized ingress and provider effects | Kautuka user authorization, run truth, approval semantics |
| Slack setup validator | Provider identity, required permissions, command publication, setup guidance | Operation-scoped provider credential and command catalogue | Bounded provider readiness result | Attachment activation, credential persistence, owner authorization, runtime state |
| Slack worker log source | Structured, privacy-safe adapter diagnostics | Adapter error/state facts | Sanitized log events | Provider calls, state mutation, authority decisions |
| ChannelHost delivery store | Lease, retry, idempotency, uncertain effect, provider receipt/exhaustion | Semantic outbound work and provider outcome | Provider-specific delivery evidence | Causing run, approval, wait, or continuation transitions |
| Provider simulator | Test-owned consumed protocol surface and deterministic faults | Authenticated test-control commands and real worker traffic | Sanitized transcript and provider-shaped responses | Production admission, Kautuka authorization bypass, runtime mutation |

Dependency direction is one way: provider adapters depend on shared semantic contracts, never Gateway domain code; projections consume canonical models but do not call mutation owners; transports call narrow provider/delivery collaborators; frontend code consumes public Gateway contracts and never infers server truth. Architecture tests enforce these boundaries.

## UpdateService

**Entry point:** `src/backend/Kautuka.UpdateService/Program.cs`

UpdateService discovers releases/catalogs, persists update state/history, stages verified artifacts, and gates apply/rollback. On Linux it invokes the root helper `kautuka-apply-release` to deploy or retarget versioned service roots; it never receives root itself. Apply/rollback continues through client disconnect and startup recovery. Production sources may be provisioned through `/etc/kautuka/update-source.json`. An empty document (no `manifestUrl` / `previewManifestUrl` / `stableManifestUrl` / `catalogManifestUrl`) is treated as unprovisioned, not a crash. Update options validation allows that unprovisioned start; discovery then returns no release, and apply stays fail-closed until a real signed feed is provisioned. The installer does not write that file when every URL is empty. `installed-linux-smoke.sh` requires `kautuka-updateservice.service` `ActiveState=active`, so an auto-restart loop fails the smoke. Release verification is fail-closed: missing signature URL, missing public key, invalid signature, or missing/mismatched artifact SHA-256 rejects apply before the helper runs. Development may set `Update:AllowUnsignedLocalFeeds=true` for unsigned local feeds.

## WebHost

**Entry point:** `src/backend/Kautuka.WebHost/Program.cs`

WebHost serves static frontend assets, falls back to `index.html`, and streams `/api/**` to Gateway. It has no domain persistence. It binds loopback HTTP on port 20229 by default. Set `WebHost:ListenAddress` to expose another interface; non-loopback configuration requires `WebHost:Tls:CertificatePath` and optionally `WebHost:Tls:CertificatePassword` for a PKCS#12 certificate. Proxy failures return a fixed sanitized response, and hop-by-hop or spoofed forwarding headers are not relayed.

## Installer

**Project:** `src/installer/Kautuka.Installer.Windows`

The Windows installer evaluates WSL prerequisites, preserves reboot-resume state, resolves/imports the appliance, waits for WebHost, and registers shortcuts/uninstall. Uninstall unregisters the distro and can delete the selected install data. Release verification limitations are tracked in the trust-boundary plan.

## Shared projects

- `Kautuka.Hosting`: network/path policy and host startup safety.
- `Kautuka.Telemetry`: logging, tracing, metrics, and health conventions.
- `Kautuka.Security`: shared key/cryptographic primitives.
- `Kautuka.*.Protos`: generated internal gRPC contracts.
- `Kautuka.Contracts` and `Kautuka.ChannelContracts`: cross-process value contracts.

\n