# Core trust-boundary hardening

**Status:** Active

**Current scope:** Implemented

**Owner:** Platform security

**Activation condition:** Immediate; findings were verified against executable code.

## Completed scope

## P0: channel secrets

- [x] Replace secret-valued ChannelHost attachment settings with CredentialsHost references and authorized runtime resolution.
- [x] Remove plaintext secret persistence and scrub existing forward-development schemas/state.
- [x] Add direct persistence, authorization, restart, and negative tests.

## P0: release verification

- [x] Require signed manifests, approved HTTPS (or loopback HTTP) sources, and artifact SHA-256 in production UpdateService, catalog, and Windows installer paths.
- [x] Reject absent/invalid signature or hash before staging/import.
- [x] Preserve an explicit development-only local-feed policy (`Update:AllowUnsignedLocalFeeds`, forced false in production) without weakening production.
- [x] Add fail-closed UpdateService and shared hash/signature verifier tests.

## P1: internal RPC least privilege

- [x] Enforce ChannelHost scoped request context and authorize operations by capability server-side.
- [x] Give Gateway and UpdateService distinct Manager mTLS identities.
- [x] Enforce exact Manager RPC allowlists; restrict UpdateService to health/status calls.

## P1: execution dependency readiness

- [x] Guard every agent execution boundary on Manager, container-runtime, and CredentialsHost readiness.
- [x] Preserve deny, cancel, and stop decisions while execution dependencies are unavailable.
- [x] Project dependency failures as canonical `RUN_ERROR` events with typed, sanitized codes.
- [x] Expose the same readiness authority through an authenticated endpoint and persistent UI banner.

## P1: durable and recoverable state

- [x] Make UpdateService and Windows installer state atomic, disk-flushed, and corruption-aware.
- [x] Back up and restore `gateway-auth.key` during update recovery.
- [x] Persist staged conversation assets and reconcile orphaned staging state after restart.
- [x] Persist agent-definition changes before deleting a referenced Skill.

## P1: authority and secret minimization

- [x] Remove the unused `provider_secrets` persistence path.
- [x] Retain the active `config_snapshots` authority and document its actual role.
- [x] Correct configuration and approval authority catalog classifications.

## P1: channel ingress durability

- [x] Persist provider cursors and ingress delivery/outbox state.
- [x] Define idempotent replay after ChannelHost or Gateway restart.
- [x] Add direct restart, disconnect, replay, and provider-boundary idempotency tests.

## P1: WebHost edge boundary

- [x] Bind to loopback by default and require configured TLS for non-loopback exposure.
- [x] Strip hop-by-hop and spoofable forwarding headers at the reverse-proxy boundary.
- [x] Stream proxied bodies and return sanitized transport failures.

## Verification completed

- [x] Focused security, restart, recovery, authorization, and persistence tests.
- [x] Full backend and frontend pre-commit gate.
- [x] Native AOT publish for Gateway, ChannelHost, UpdateService, Manager, and WebHost.
- [x] Live first-run onboarding and local-provider browser validation.
- [x] Live browser outage and recovery validation for Manager, CredentialsHost, and Podman, including pre-run blocking and post-reconcile execution.
