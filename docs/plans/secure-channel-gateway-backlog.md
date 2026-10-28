# Secure Channel Gateway and native channel experience backlog

**Status:** Active

**Owner:** Platform security and channel experience

**Activation condition:** Immediate. The reusable contract and native channel experience must be delivered without weakening the existing Gateway, CredentialsHost, ChannelHost, session, approval, or audit authority boundaries.

## Product boundary

Evolve ChannelHost into a product-grade, independently testable Channel Gateway that Kautuka consumes through a versioned contract. The gateway may later support other authorized runtimes, but Kautuka remains its first authority and reference consumer.

The Channel Gateway owns provider transport, provider-native rendering, interaction acknowledgement, provider capability degradation, attachment transfer, and durable provider delivery. Kautuka Gateway continues to own authentication, user and tenant authority, agent and target selection, canonical sessions and runtime events, approvals and continuations, policy, audit, and the meaning of every interaction.

The work is not complete if native channel UI improves by moving session, approval, credential, authorization, or continuation truth into a provider adapter, client callback, in-memory handler, or external framework.

## Outcome

An authorized Kautuka user can use an agent through Slack and Telegram with provider-native streaming, structured messages, files, commands, reactions, and durable approvals. The same semantic message and interaction contracts can be consumed by another authorized runtime without granting that runtime Kautuka authority or requiring Kautuka-specific domain types.

Every accepted request, ingress event, interaction, credential use, and outbound effect must be attributable to an authenticated workload, authorized actor, tenant, owner scope, registration, conversation, request, and durable idempotency identity.

## Non-negotiable security invariants

- Treat the Gateway-to-Channel-Gateway connection as a hostile boundary even when both processes run on the same machine.
- Require mutually authenticated, encrypted transport. Never add plaintext HTTP/2, opportunistic TLS, insecure certificate bypasses, or a development fallback that can activate in production.
- Bind every client certificate or workload identity to an exact server-side service identity and method allowlist. Trusting any certificate issued by the local CA is insufficient.
- Never derive authority from request-supplied capability names. Request claims may describe intended attribution, but only server-verified workload policy and a cryptographically verified delegation may grant authority.
- Bind delegated actor authority to audience, issuer, subject, tenant, owner/resource scope, permitted operation, request digest, issued-at time, expiry, and a unique replay identity.
- Authorize the concrete attachment, registration, conversation, media object, interaction, and owner on every call. Possession of a broad channel capability must not imply cross-user or cross-tenant access.
- Keep provider credentials encrypted in CredentialsHost and expose resolved values only to the exact provider attachment and operation that needs them. Never persist plaintext credentials, include them in message documents, or return them through diagnostics.
- Run production Gateway and ChannelHost under distinct operating-system principals. Private TLS and delegation material must be owned by only the service that consumes it; installer and upgrade preflight must fail closed on missing files, unexpected files, ownership drift, or relaxed permissions.
- Persist authoritative ingress and interaction state before acknowledgement whenever provider timing permits. When a provider requires acknowledgement first, persist the minimum authenticated delivery claim locally before any asynchronous business processing.
- Make every ingress event, callback, command, media fetch, outbound operation, edit, and approval resolution replay-safe and idempotent.
- Treat provider payloads, message markup, files, callback values, and external identifiers as untrusted input. Validate type, size, encoding, ownership, expiry, and allowed values before use.
- Fail closed on identity, delegation, policy, clock, contract-version, schema, signature, resource-scope, or replay-validation failure.
- Emit sanitized audit evidence for authorization decisions without recording credentials, delegation tokens, prompts, tool arguments, message bodies, or reusable provider callback secrets.
- A compromised provider account must not grant Kautuka application authority, and a compromised channel registration must not cross its owner, tenant, target, or credential scope.

## Explicit trust model

The security design must state which components are trusted and what compromise of each component permits. It must not use the word "secure" to hide an undefined transitive trust relationship.

- **Kautuka Gateway:** trusted authority for authenticated users, tenants, agents, sessions, approvals, policy decisions, and delegation issuance.
- **Channel Gateway:** trusted only for the provider attachments assigned to it, native rendering, transport delivery, durable channel ingress, and verification of Gateway-issued delegation. It must not mint user authority or invent approval outcomes.
- **CredentialsHost:** sole durable authority for provider secrets. Channel Gateway receives only operation-scoped resolved material and keeps it in process memory for the minimum necessary lifetime.
- **Provider:** untrusted external transport. Provider identity and signatures establish the provider source, not Kautuka user authority.
- **External reusable consumer:** untrusted until separately registered as a workload, assigned an explicit method/resource policy, and given its own delegation issuer or a supported authorization exchange. It never inherits the Kautuka Gateway identity.

No design can make a fully compromised trusted authority harmless. The target is a fail-closed boundary with explicit trust anchors, least privilege, bounded blast radius, replay resistance, evidence, and automated adversarial proof.

## P0 - authenticated and authorized service boundary

### CHG-001: Threat model and security ADR

Create an ADR covering assets, trust anchors, entry points, identities, delegated authority, data flows, attacker capabilities, compromise boundaries, revocation, recovery, and accepted residual risks. Include malicious local processes, stolen or misissued certificates, replay, confused-deputy calls, cross-owner access, provider forgery, callback tampering, credential theft, contract downgrade, oversized payloads, and compromised external consumers.

The ADR must select the exact workload-identity and delegation formats before the reusable contract is frozen.

### CHG-002: Exact workload identity and mutual TLS

Replace CA-membership-only client acceptance with exact workload identity validation. Validate certificate chain, EKU, subject/SAN identity, validity window, expected issuer, and an active pinned identity or public-key record. Map the authenticated identity to a server-side method allowlist.

Issue distinct identities for control-plane administration, runtime delivery, and canonical ingress consumption unless the ADR proves that one identity has equal or smaller blast radius. Define rotation with an explicit overlap window, atomic activation, revocation, stale-identity rejection, and recovery that does not silently broaden trust.

The client must continue to validate the exact Channel Gateway server identity and pinned trust root. Production must reject missing, expired, not-yet-valid, wrong-purpose, wrong-name, revoked, or unexpected identities.

### CHG-003: Signed, short-lived actor delegation

Replace authority-bearing `actor_capabilities` request fields with a signed delegation issued only after Gateway authentication and authorization. Channel Gateway must verify issuer, audience, subject, tenant, owner/resource constraints, permitted operation, request digest, nonce or token ID, issued-at time, expiry, signature algorithm, and signing-key status.

Keep transport identity and actor delegation separate: mutual TLS answers which workload called; delegation answers on whose behalf and for which exact operation it may act. A valid delegation over an unauthorized workload connection, or a valid workload identity with a missing/invalid delegation, must fail closed.

Define signing-key storage, rotation, overlap, revocation, clock-skew bounds, token lifetime, and incident response. Do not use provider credentials, the Gateway authentication cookie key, or a general application secret as the delegation signing key.

### CHG-004: Server-side method and resource authorization

Create one declarative authorization matrix covering every RPC and stream operation. For each method define allowed workload identities, required delegated operation, owner and tenant rules, resource lookup, administrator rules, and whether delegation is required.

Resolve attachment ownership, registration state, provider, conversation, media, and interaction identity from authoritative Channel Gateway state rather than accepting them as proof from the caller. Reject owner/resource mismatches before adapter invocation or secret resolution.

Keep ingress subscription exclusive to the canonical registered consumer identity. Bind acknowledgements to the same subscriber, delivery, event, and lease generation that received the event.

### CHG-005: Request integrity, freshness, replay, and idempotency

Define a canonical request digest covering method, contract version, resource identity, payload digest, idempotency key, and relevant correlation fields. Bind the signed delegation to that digest.

Persist or otherwise atomically enforce bounded replay detection for privileged mutations and callbacks. Reusing a delegation or idempotency key with a different digest must be a security failure. Reusing it with the same digest must return or reconcile the original durable outcome rather than repeat the provider effect.

Provider reconciliation must remain capability-specific. Semantic Slack creates carry a stable `client_msg_id`. Telegram exposes no equivalent atomic idempotency primitive, so an uncertain Telegram effect at `EFFECT_STARTED` must stop for reconciliation and must never be automatically replayed. The acceptance claim is no unproven repeat, not universal provider-level exactly-once delivery.

Specify reconnect and restart behavior for streaming ingress, acknowledgement, outbound delivery, media reads, edits, deletes, interaction resolution, and provider retries.

### CHG-006: Credential isolation and operation-scoped resolution

Keep credential references in Channel Gateway attachment state and plaintext secrets in CredentialsHost only. Replace broad credential hydration with an operation-scoped resolution contract where practical, binding the request to workload, attachment, provider, field set, purpose, and expiry.

Prevent credentials from entering semantic UI documents, durable ingress payloads, outbound payload history, exception text, traces, metrics, crash dumps, or provider error responses. Zero or release temporary secret buffers when supported and remove resolved credentials immediately when an attachment is deactivated or authorization changes.

### CHG-007: Contract negotiation and downgrade resistance

Version the transport, authorization envelope, semantic message document, provider capability document, interaction contract, and error taxonomy independently. Negotiate only explicitly supported versions and fail closed on unknown security-critical fields or downgrade attempts.

Apply strict message and collection limits before allocation and deserialization. Reject duplicate fields, invalid discriminators, unsupported markup, malformed Unicode, path-like file names, unsafe media types, oversized content, excessive nesting, and decompression or archive bombs.

### CHG-008: Security audit and privacy-safe diagnostics

Record authenticated workload identity, delegated actor and tenant identifiers, authorization policy/version, method, resource identifiers, request and payload digests, idempotency identity, decision, denial reason code, certificate/signing-key identifiers, and resulting durable operation identity.

Do not log raw delegation tokens, provider secrets, callback secrets, message bodies, prompt content, attachments, or native provider payloads by default. Add access-controlled diagnostic capture only as an explicit temporary mode with redaction, retention, and audit.

### CHG-009: Adversarial trust-boundary verification

Add automated negative and restart tests for unauthorized certificates, alternate CA-issued certificates, wrong EKU/SAN/subject, expired and not-yet-valid certificates, revoked identities, stale rotation identities, wrong workload role, missing or forged delegation, wrong audience/issuer, expired delegation, clock skew, replay, changed payload under the same token or idempotency key, owner and tenant confusion, registration substitution, media substitution, forged callbacks, contract downgrade, malformed payloads, oversized inputs, and secret leakage.

Exercise process restart and network interruption during certificate rotation, delegation verification, ingress persistence, acknowledgement, outbound send, provider callback, and approval resolution. Security tests must prove both denial and absence of partial side effects.

## P0 - reusable semantic channel contract

### CHG-010: Semantic message document

Define a serializable, provider-neutral message document with a deliberately small initial vocabulary: message, header, section/markdown, fields, context, image, table, divider, actions, button, select, and input. Keep it data-only; do not serialize delegates, closures, executable code, provider credentials, or native provider payloads.

Provide reviewed Kautuka templates for agent response, approval request, approval resolution, run progress, command help, error, file delivery, and status summary. Domain services create semantic intent; provider renderers own native layout.

### CHG-011: Structured provider capabilities and budgets

Replace capability-version strings as the only capability signal with a structured, versioned capability document covering streaming, edits, deletes, typing, reactions, modals, ephemeral messages, files, history, commands, suggested prompts, thread titles, message limits, action limits, and acknowledgement deadlines.

Capabilities are descriptive, not authority. The server must verify requested operations independently and renderers must still enforce native provider limits.

### CHG-012: Durable interaction document

Represent every interactive control with stable data: interaction ID, action ID, owning user/tenant/registration/conversation/session, allowed values, purpose, expiry, document version, approval request when applicable, and terminal state. Provider callback payloads carry only an opaque bounded identifier and permitted small value.

Resolve callbacks against durable state. Enforce first-valid-resolution, exact actor eligibility, expiry, duplicate resolution, late arrival, web-versus-channel races, restart recovery, and in-place terminal rendering without allowing the adapter to resume an agent independently.

### CHG-013: Native rendering and deterministic degradation

Create one renderer per provider that converts the semantic document into native payloads and enforces every provider budget. Define deterministic degradation from rich controls to simpler controls and then structured text. Never silently omit an approval decision, security warning, required field, or terminal state.

Make rendering pure and testable without provider credentials or network access. Retain optional native escape hatches only behind explicit reviewed provider-specific document types, not arbitrary raw JSON supplied by agents or external consumers.

### CHG-014: Streaming and delivery coordinator

Convert canonical assistant deltas into provider-appropriate delivery: native streaming where available, throttled edits with stable chunk boundaries, rollover at provider limits, or buffered final delivery for immutable surfaces. Preserve Kautuka's durable outbound intent and retry authority.

Define cancellation, approval interruption, tool-progress, partial-send, edit failure, reconnect, duplicate send, and terminal finalization behavior. A displayed partial message must never be mistaken for a committed run outcome.

### CHG-015: Adapter conformance kit

Create a shared adapter test suite covering ingress normalization, thread and reply targeting, semantic rendering, budgets, degradation, streaming, interaction acknowledgement, stable action identity, files, reactions, commands, duplicate delivery, idempotency, restart replay, credential isolation, sanitized errors, and unsupported-capability behavior.

Provide deterministic fake provider and fake consumer implementations. No adapter may be registered in production until it passes the common conformance suite and its provider-specific adversarial tests.

## P1 - provider-native experiences

### CHG-016: Slack reference implementation

Use Slack as the reference renderer and parity implementation. Add Block Kit-style messages, native streaming or throttled edits, files, reactions, commands, modals where justified, ephemeral delivery, thread history, user lookup, and in-place approval resolution. Preserve Socket Mode replay, bot-echo filtering, multi-registration isolation, and the secure Channel Gateway boundary.


Add Components V2-style structured messages, typing, streamed edits, reactions, files, commands, user lookup, and provider-limit degradation. Preserve gateway reconnect and sequence behavior, interaction acknowledgement deadlines, multi-registration isolation, and durable callback handling.

### CHG-018: Telegram parity and refinement

Move Telegram onto the semantic document without regressing long-polling cursor durability, single-chat agent routing, attachments, typing, commands, inline approvals, or restart replay. Agent/session selection belongs to Kautuka; Telegram topics are not part of the active routing UX. Add chunked streaming, reactions, deterministic HTML rendering, and explicit fallback behavior for unsupported modal and ephemeral concepts.

### CHG-019: WhatsApp polling transport decision

**Status:** Deferred by product owner; revisit later. Not rejected.

Kautuka's channel transport requirement is polling or an outbound-initiated persistent connection for every admitted provider. Inbound webhooks are not permitted. Meta's currently documented WhatsApp Cloud API delivers inbound messages and message-status changes through webhooks and does not document an inbound message-list, cursor, long-poll, or outbound persistent-connection API.

Do not resolve this conflict implicitly. Do not add an inbound webhook, silently remove WhatsApp from the intended provider set, or ship unofficial WhatsApp Web/browser automation or a third-party inbox dependency. Keep the adapter unadmitted while deferred; revisit only when the product owner reopens CHG-019 or an official polling-compatible transport is verified.

## P1 - operations and lifecycle

### CHG-020: Registration and policy lifecycle

Define create, validate, provision, activate, rotate, suspend, deactivate, delete, and recover transitions for provider registrations. Every transition must be authorized, idempotent, audited, and reflected in readiness. Suspension or authority removal must stop new ingress execution and outbound delivery without deleting evidence required for reconciliation.

### CHG-021: Abuse, rate, and resource controls

Add per-workload, tenant, owner, registration, provider, conversation, and operation limits for connection count, ingress rate, concurrent runs, callbacks, media bytes, outbound queue depth, edit frequency, retries, and storage. Provider backpressure and abuse must degrade one registration without exhausting the host or crossing tenant boundaries.

### CHG-022: Operational visibility

Expose provider capability/version, registration readiness, authenticated connection identity, certificate and delegation-key health, ingress lag, acknowledgement age, outbox depth, retry state, streaming/edit state, callback expiry, authorization denials, rate limiting, and reconciliation requirements.

Keep operator actions behind explicit authorization and record every manual retry, suspension, credential rotation, replay, or terminal disposition.

### CHG-023: Schema and state migration

Version Channel Gateway state independently from Kautuka Gateway state. Define forward migration, backup, integrity checks, compatibility windows, failed-migration recovery, and update/rollback constraints. Never accept a mixed contract or schema state that bypasses authorization or changes interaction meaning.

## P2 - reusable boundary and productization

### CHG-024: External consumer contract

Document the minimum runtime-facing contract independently of Kautuka domain types. An external consumer must supply an authenticated runtime, stable session mapping, a registered workload identity, a supported delegation issuer or authorization exchange, durable interaction semantics, and required state providers.

External consumers receive no Kautuka user, tenant, credential, approval, or administrative authority by default. Registration must be explicit and least-privileged, and must not reuse the Kautuka Gateway workload identity.

### CHG-025: Packaging without operational fragmentation

Keep the implementation in the Kautuka repository until extraction is earned. Make contracts, renderers, conformance tests, and the Channel Gateway executable independently buildable and testable. Do not create a separate repository, release train, installer, brand, or support promise while Kautuka remains the only real consumer.

### CHG-026: External reference consumer

Integrate one non-Kautuka runtime in a controlled environment using only the documented public contract. Prove workload isolation, delegation, session continuity, durable approvals, restart recovery, provider-native rendering, credential separation, and complete revocation without Kautuka-specific backdoors.

The reference process satisfies this technical gate by invoking Channel Gateway over the public gRPC contract from an independent executable. This is controlled interoperability evidence, not evidence of an independent adopter or permission to extract the subsystem as a separate product.

### Current automated acceptance evidence

- CHG-010 through CHG-015: the shared semantic, capability, interaction, rendering, streaming, and adapter-conformance suites run against every admitted provider: Slack and Telegram.
- CHG-016 through CHG-018: production ingress is outbound-only from Kautuka's network boundary: Slack obtains a Socket Mode URL through `apps.connections.open`, and Telegram advances a durable `getUpdates` long-poll cursor.
- CHG-019: no provider webhook listener is implemented. WhatsApp is explicitly deferred and the admission policy rejects it with `whatsapp_chg019_deferred_pending_official_polling_compatible_transport`.
- CHG-020: lifecycle transitions, authority removal, durable mutation identity, uncertain-effect reconciliation, and provider readiness are covered by restart and security suites.
- CHG-024 through CHG-026: public packages build independently and the external-process proof crosses the mTLS public contract without Gateway or ChannelHost implementation dependencies; restart, approval resolution, denied credential/administrative authority, and revocation are exercised.
- CHG-027 remains a product decision gate, not a completion claim. The controlled reference is not an independent adopter, and live provider release gates still require administrator-created Slack and Telegram test applications.

### CHG-027: Productization decision gate

Consider a standalone Channel Gateway product only after:

- Slack, Telegram, and the common conformance suite are production-ready in Kautuka;
- every admitted provider, including WhatsApp if admitted, satisfies the polling or outbound-initiated persistent-connection boundary without inbound webhooks;
- at least one real external consumer operates through the public boundary;
- external operation does not require duplicating the full Kautuka administration and Agent Operations surfaces;
- security ownership, vulnerability response, compatibility, documentation, packaging, and support have named owners; and
- the standalone value is durable, secure agent-channel operations rather than another generic messaging adapter library.

## Security completion gate

Do not call the Channel Gateway boundary secure or reusable until automated evidence proves:

- only an exact active workload identity can establish the production connection;
- every RPC and stream method is authorized from server-side workload policy and verified delegation, never request-supplied capability strings;
- owner, tenant, registration, conversation, media, interaction, and approval scopes cannot be substituted;
- request replay, callback replay, duplicate delivery, and idempotency-key conflict cannot duplicate effects or change outcomes;
- certificate and signing-key rotation, revocation, restart, and recovery fail closed without prolonged outage or trust broadening;
- provider credentials never persist or appear in logs, traces, messages, errors, exports, or unauthorized process memory;
- malformed, oversized, downgraded, forged, expired, or unauthorized traffic is denied before adapter invocation and leaves no partial side effect;
- a compromised registration cannot cross its configured owner, tenant, target, session, or credential boundary; and
- an operator can explain every accepted and denied privileged operation from privacy-safe durable evidence.

## Experience completion gate

Do not call the native channel experience complete until:

- one semantic document renders deterministically across Slack and Telegram;
- Slack no longer depends on Telegram-shaped inline-keyboard semantics;
- streaming, edits, files, commands, reactions, interactions, and unsupported capabilities behave according to the declared provider contract;
- pending approvals survive process restart and resolve exactly once from web or channel;
- terminal approval and run state updates in place where supported and degrades clearly where not supported;
- provider limits never produce rejected, malformed, misleading, or silently incomplete security-critical messages;
- every adapter passes the common conformance and provider-specific adversarial suites; and
- the existing channel durability, credential isolation, routing, attribution, and audit guarantees have no regression.

## Explicit non-goals

- Adding a Node sidecar solely to consume a third-party chat transport.
- Replacing Kautuka's canonical sessions, runtime events, approvals, continuations, credentials, or audit with adapter-owned state.
- Trusting provider identity as Kautuka user identity without an explicit verified mapping.
- Letting external consumers self-assert capabilities, owners, tenants, administrative roles, or Kautuka identities.
- Exposing raw provider payloads or arbitrary executable UI components as the reusable contract.
- Building a general-purpose layout engine or matching every provider feature before the reference Slack experience proves the semantic vocabulary.
- Creating a standalone repository, SKU, or product suite entry before the external-consumer and productization gates are satisfied.
