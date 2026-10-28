# ADR 0003: Gateway-to-Channel Gateway is an authenticated delegated boundary

**Status:** Accepted
**Date:** 2026-07-18

## Decision

ChannelHost is a separate security authority, even when it runs on the same machine as Gateway. Every connection uses TLS 1.2 or later with mutual authentication. ChannelHost accepts only a CA-valid, client-auth leaf whose exact common name is `Kautuka Gateway Client` and whose URI SAN is `spiffe://kautuka.local/workload/gateway/channel-control`. CA membership alone grants no access. Gateway pins the ChannelHost CA and the exact `Channel Server` server identity.

Every RPC, including `Ping`, additionally requires authorization envelope `kautuka-channel-auth/v1`. Gateway signs it with the dedicated Channel delegation key, not a cookie, provider credential, transport key, or general application secret. The signed claims bind issuer `kautuka-gateway`, audience `kautuka-channel-gateway`, actor, tenant, session, request ID, operation, contract version, issued/expiry time, token ID, key ID, and the SHA-256 digest of the canonical protobuf request. Lifetime is 45 seconds with at most 10 seconds of clock skew.

Caller-supplied `actor_capabilities` is wire-retained during migration but is authority-free and rejected when populated. ChannelHost's server-side matrix is authoritative:

| RPC | Delegated operation | Resource rule |
|---|---|---|
| `Ping` | `channels.read` | exact mTLS workload plus signed short-lived delegation; no health data is returned before both checks |
| `ListProviders` | `channels.read` | no owner resource |
| `GetAttachments`, `GetAttachment`, `GetAttachmentSchema` | `channels.read` | actor must equal authoritative attachment owner |
| `UpsertAttachment`, `SyncOwnerTargets`, `ProvisionAttachment`, `DeactivateAttachment` | `channels.write` | actor must equal requested and/or stored owner before adapter use |
| `SetRuntimeCredentials`, `ReadInboundMedia`, `SendOutboundMessage` | `channels.runtime` | stored attachment owner must equal actor |
| `StreamIngress`, `AcknowledgeIngress` | `channels.ingress` | canonical `gateway-ingress` subscriber only |

One exact Gateway workload identity is deliberately used for the four operation classes. Separate transport certificates would not reduce the blast radius while all four clients execute in the same Gateway process and share its memory. Least privilege is instead enforced per signed request by the operation matrix. A future independently deployed consumer must receive a distinct workload identity and server-side method policy; it never inherits this identity or issuer.

Replay token IDs and mutation operation records are persisted in ChannelHost SQLite. A token cannot be reused. Operation identity is scoped by authenticated workload, tenant, actor, method and idempotency key, so one workload or tenant cannot reserve another's key. Pure request and resource validation happens before reservation. The durable state machine distinguishes `RESERVED`, `EFFECT_STARTED`, and `COMPLETED`: an expired pre-effect reservation can be safely re-leased after restart, a completed operation returns its cached result, and an uncertain provider effect requires reconciliation rather than being repeated. Reusing a scoped key for a changed business digest is denied. Security audit events contain workload/policy context, privacy-safe hashes of actor, tenant, request and token IDs, public key ID, method, digest, decision and reason code; they exclude raw identity values, tokens, secrets, message bodies, native provider payloads and attachments.

Production private-key ownership is disjoint. Installer/bootstrap creates the CA offline, provisions the ChannelHost server PFX to a ChannelHost-only private root, provisions the client mTLS and delegation-signing PFX files to a Gateway-only private root, and publishes only public CA/client/delegation certificates into role-specific trust roots. The CA signing key is retained only in a root-owned offline-authority root that neither runtime service principal can read. ChannelHost refuses production startup without the public-only inventory and never creates or reads Gateway private keys. Gateway refuses production startup without its private inventory and public ChannelHost trust. Development retains an explicit `Development`-environment bootstrap with a critical warning; it is not available through an ordinary production setting and is not a hostile-process boundary.

Client and delegation rotation uses an exact fixed-slot inventory: `next`, `current`, and `previous`; arbitrary additional files still fail startup. Staging verifies both new private leaves against the current CA and exact CN/URI identities, and verifies their public counterparts. Activation promotes both identities as one recoverable operation, writes a thumbprint-bound manifest, and allows current plus previous identities for a bounded overlap (15 minutes by default and never more than 24 hours). ChannelHost restarts before Gateway after activation so it accepts both identities before Gateway presents the new current identity. Rollback restores both previous leaves and retains the failed leaves in `next`; finalization removes `previous` only after overlap expiry. After expiry ChannelHost reports the old thumbprints as revoked and rejects them even before finalization removes the recovery files.

Directory separation under one operating-system identity is not isolation. Production runs Gateway and ChannelHost under distinct service principals or container user/mount domains and grants each private root only to its owner. The supported Linux/WSL installer creates separate system users and units, verifies both cross-read denial directions with `runuser`, validates exact UID/GID/mode and inventory, and starts ChannelHost before Gateway. Any legacy or manually assembled single-principal native deployment fails the hostile same-user-process gate and is Development-only for this boundary.

## Threat model

Protected assets are provider credentials, canonical owner/tenant mappings, channel registration state, inbound media, durable ingress, outbound effects, approvals/interactions, and security evidence. Entry points are the ChannelHost gRPC listener, provider transports/callbacks, local certificate files, provider media reads, and future external consumers.

Assumed attackers include a malicious local process without access to protected service-state files; a remote provider participant; a holder of an arbitrary certificate from the local CA; a caller replaying or modifying captured requests; a confused Gateway client presenting another owner's IDs; a forged provider callback; and a compromised future consumer. The boundary denies wrong CA, identity, purpose, SAN, validity, issuer, audience, operation, owner, tenant, digest, nonce, time window, key, subscriber, or contract version before adapter invocation or secret resolution.

Provider authentication proves only provider source. It never establishes Kautuka actor authority. Provider callback data remains an opaque bounded identifier and must resolve to durable Kautuka state. Credential references may be durable; plaintext provider secrets remain operation-scoped runtime material and must not enter attachment settings, semantic documents, logs, traces, exceptions, or provider-facing error details.

Callback resolution additionally requires exact equality with the durable interaction's registration, conversation/chat, normalized thread, source transport message, intended user and pending option. A valid opaque token copied to another bot, chat, thread or message cannot resolve the interaction.

Large or malformed messages remain constrained by gRPC limits and the semantic/provider contract validators. Contract negotiation is explicit; unknown security authorization or contract versions fail closed. New security-critical fields require a new authorization version rather than permissive ignoring.

## Compromise boundaries and residual risks

A fully compromised Gateway can issue valid delegations for any actor known to it; cryptography cannot make a trusted issuer harmless. A fully compromised ChannelHost can use in-memory provider credentials and emit provider effects. Host-administrator compromise can read protected local keys. The controls bound lesser compromises, provide evidence, and prevent an arbitrary local/CA-issued client, provider callback, captured request, or cross-owner substitution from becoming Gateway authority.

An outbound provider effect followed by process loss before its outcome is durably recorded cannot always be queried from every provider. Kautuka fails closed with `idempotent_operation_in_progress` rather than repeating that effect. Provider-specific reconciliation may later turn that indeterminate state into a confirmed outcome; it must not weaken the no-repeat invariant.

For semantic sends, Slack receives a deterministic `client_msg_id` derived from Kautuka's scoped idempotency key. This provider-native marker supports reconciliation and suppresses a repeated provider create. Telegram's Bot API has no equivalent atomic idempotency marker or query-by-client-key contract. Consequently, a Telegram operation left at `EFFECT_STARTED` is never replayed automatically: it remains reconciliation-required for an operator or a future provider-supported proof of outcome. Kautuka does not claim provider-level exactly-once delivery where the provider cannot prove it.

Canonical ingress delivery carries a durable lease generation. ACK must present the exact current lease; an ACK from a prior delivery cannot delete a record that has since been re-leased. Rate windows are keyed by the authenticated workload policy identity and persisted so restart does not reset security quotas.

## Recovery and verification

Recovery never broadens trust: restore the protected CA/key inventory, revoke unexpected leaves/keys, rotate the delegation key with bounded overlap, restart Gateway so it loads the active key, and then reopen ChannelHost. Do not temporarily fall back to CA-only validation, unsigned contexts, plaintext HTTP/2, or client capability lists.

Permanent CI covers exact identity, missing/forged/expired delegation, request substitution, replay, idempotency conflict, owner mismatch, secret canaries and absence of duplicate effects. Scheduled/release tests cover certificate rotation overlap, process restart, network interruption, replay storms and provider recovery. A denial test passes only when the expected reason is recorded and no adapter call, credential resolution, state mutation, acknowledgement, or provider effect occurred.

Configured external consumers are admitted through an exact workload policy containing active certificate thumbprints, CN and URI identity, method and operation allowlists, a distinct delegation issuer, audience and key set, and an explicit owner allowlist. An empty external owner allowlist denies all owner-scoped methods; only the built-in Gateway policy has an explicit owner wildcard. External policies cannot resolve provider credentials or invoke lifecycle administration even if those method names are accidentally added to their allowlist. Removing the policy or thumbprint revokes the workload on the next connection without broadening trust.

Outbound assets cross the boundary only through the client-streaming `UploadOutboundAsset` RPC after Gateway resolves the exact asset against canonical session and owner state. The signed first frame binds asset, owner, tenant, registration, conversation and thread, safe name, media type, size, SHA-256 and idempotency identity. ChannelHost enforces bounded chunks and totals, verifies size and digest before availability, and exposes only an opaque upload ID to delivery actions. Owner-protected transient spool files are atomically claimed for one delivery and deleted after confirmed provider success, expiry, deactivation, suspension or deletion. A crash after delivery claim fails closed for reconciliation rather than repeating an uncertain provider effect.

## Consequences

- ChannelHost remains independently reusable without becoming an unauthenticated generic relay.
- Gateway and ChannelHost must coordinate certificate/delegation rotation and contract versions.
- Requests have small signing, verification and SQLite replay costs.
- Health checks require exact mTLS and signed delegation, and report the actual authenticated workload, certificate/delegation validity, rotation phase, overlap expiry, and revoked key IDs.
- External consumers require explicit workload registration and cannot reuse Gateway credentials.
