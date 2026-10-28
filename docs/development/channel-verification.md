# Channel completion checklist

Use this checklist before marking channel transport, Channel Gateway contract, setup, lifecycle, native rendering, or channel-facing runtime work done. It complements—not replaces—the [browser completion checklist](browser-verification.md). It is a developer and release verification aid, not a substitute for automated tests or a provider security review.

For the CRH-030 cross-surface automation mapping, see [cross-surface acceptance](cross-surface-acceptance.md). Simulator-only results never replace the mapped live-provider checks.

## Reporting rule

Name the scope accurately:

- **Automated channel pass** — all applicable local contract, security, integration, and simulated-provider scenarios below passed. It does not prove live provider installation, permissions, or delivery.
- **Full live channel pass** — the automated pass plus every applicable live-provider scenario below, using disposable Slack and Telegram test installations, passed.

Never report a full live pass when a provider, scenario, credential, or external-provider condition was skipped, blocked, or inferred from a mock. Record every exclusion and its reason. WhatsApp is explicitly deferred and is not an applicable scenario unless product direction changes.

## Transport rule

Kautuka admits no inbound webhooks. Verify the transport actually used by each provider:

| Provider | Required transport | Prohibited substitute |
|---|---|---|
| Slack | Outbound-initiated Socket Mode connection | Events API webhook receiver |
| Telegram | Bot API long polling | `setWebhook` or an HTTP webhook receiver |

Do not add a public listener, tunnel, callback endpoint, or provider webhook secret to make a test easier.

## Prepare the environment

1. Record the branch, commit, build mode, and contract versions under test.
2. Build ChannelHost, Gateway, CredentialsHost, and the frontend from that commit; run the repository validation gate and applicable ChannelHost/Gateway test slices.
3. Start CredentialsHost, ChannelHost, Gateway, Manager, UpdateService, and WebHost. Use a clean development data set or identify the disposable owners, attachments, conversations, channels, and test messages created.
4. For live testing, create separate disposable provider installations and test channels. Enter credentials only through Kautuka's setup UI; never paste tokens into chat, source, logs, test evidence, or configuration files.
5. Open browser Console and Network panels, Gateway/ChannelHost logs, and provider-side event or audit views where available. Redact identifiers and delete/revoke disposable credentials after testing.

## Automated preflight

Run every applicable scenario before a live test. Mocks may simulate provider APIs but must not bypass the Gateway-to-ChannelHost security boundary.

| ID | Scenario | Expected result |
|---|---|---|
| P-01 | Build, contract/package, and ChannelHost/Gateway test suites. | All pass; generated protobuf and public contract packages are current. |
| P-02 | Provider admission and transport configuration for Slack and Telegram. | Only the required outbound/polling transport starts; webhook configuration is rejected or absent. |
| P-03 | Exact mTLS identity and short-lived signed delegation. | Chain, EKU, CN/URI identity, pinned key, audience, actor, tenant, method, digest, nonce, and expiry are all enforced; malformed, replayed, expired, or cross-method requests fail closed. |
| P-04 | Authorization and resource binding. | Every RPC, including Ping, is authorized; owner, tenant, attachment, registration, conversation, thread, media, and callback scope mismatches are denied and durably audited without raw secrets or message bodies. |
| P-05 | Credential boundary. | The browser stores credentials through Gateway/CredentialsHost; ChannelHost receives only runtime values, persists no plaintext secret, and logs/tests expose no token or provider body. |
| P-06 | Lifecycle and rotation. | Owner-scoped activate, suspend, recover, replacement-credential, revocation, bounded current/previous certificate overlap, expiry, rollback, and stale-leaf rejection behave deterministically. |
| P-07 | Ingress durability. | A provider event is committed before exposure; acknowledgement occurs after Gateway processing; duplicate delivery, cursor replay, restart, and reconnect produce one canonical event and no loss. |
| P-08 | Outbound idempotency and uncertain effect handling. | Same scoped idempotency key returns the cached result; changed digest is denied; pre-effect lease recovery is safe; post-effect uncertainty requires reconciliation and never auto-repeats. |
| P-09 | Native semantic contract. | Slack Block Kit and Telegram native rendering deterministically honor capability limits; required approval/security/terminal information is never silently removed. |
| P-10 | Streaming, interactions, files, commands, and reactions. | One durable provider mapping per stable stream; stream updates finalize once; actions resolve first-valid-wins after restart; files stay opaque/bounded/owner-scoped; commands and reactions are attributed and deduplicated. |
| P-11 | Limits and error hygiene. | Payload/media/websocket limits, bounded retry/backoff, rate governance, malformed provider data, and provider faults return bounded sanitized outcomes without exception leaks, provider bodies, or secrets. |
| P-12 | External consumer compatibility. | The standalone contract sample calls the authenticated Channel Gateway gRPC contract from an independent process; it does not reference Kautuka Gateway or ChannelHost implementation assemblies. |

## Setup experience and live provider validation

Run all rows for each admitted provider. A setup flow is not complete merely because a developer can obtain a token manually: an average owner must be guided through the remaining provider-side steps, see exact missing permission/action feedback, and reach an active attachment without guessing IDs or endpoints.

| ID | Scenario | Expected result |
|---|---|---|
| L-01 | Begin setup as the attachment owner in Settings. | Provider choice, plain-language prerequisites, safe official links, required permissions, and what Kautuka will validate are visible; no secret is prefilled or echoed. |
| L-02 | Enter deliberately incomplete credentials/configuration. | Validation fails with a bounded, actionable explanation of the next provider-side action; no raw response, token, native payload, or hidden numeric identifier is requested. |
| L-03 | Complete setup with disposable credentials. | Slack validates Socket Mode and required scopes; Telegram validates long-poll access and webhook absence. The attachment becomes active once, under the correct owner. |
| L-04 | Destination/channel-target selection. | Telegram accepts the owner-entered private-chat ID with clear setup guidance; Slack learns the owner and default DM destination from a one-time pairing code, while conversation and thread activity supplies channel routing. Cross-owner and cross-registration attempts fail closed. |
| L-05 | Send an ordinary user message in the configured provider destination. | One attributed canonical Kautuka turn is created and one answer is delivered. Reconnect/poll replay does not duplicate it. |
| L-06 | Send a native command and a reaction where the provider supports them. | The action reaches the intended Kautuka session once, has correct actor/channel attribution, and produces the documented result. |
| L-07 | Request an approval and resolve it from the provider, then repeat resolution from the web UI. | The first eligible resolution wins; terminal rendering updates in place where supported; the agent continuation is owned by Gateway and resumes/ends exactly once. |
| L-08 | Trigger a streamed response, then a message with a disposable small supported file. | Streaming remains one provider message with correct final state; file is admitted only for the bound owner/conversation, is delivered once, and no signed URL or bytes appear in durable evidence. |
| L-09 | Suspend the attachment while it is active, verify traffic stops, then recover with valid replacement configuration. | Suspension removes runtime access and blocks traffic without losing audit evidence; recovery returns to inactive until revalidated, then active exactly once. |
| L-10 | Revoke or replace the disposable provider credential and restart ChannelHost/Gateway during a pending ingress, outbound send, and approval wait. | The attachment fails safely, no provider effect is duplicated, pending work recovers/reconciles explicitly, and repaired credentials require the owner-scoped recovery path. |
| L-11 | Inspect provider settings and Kautuka logs after testing. | No webhook receiver is configured; outbound Socket Mode/Gateway/polling remains the only ingress path; logs/audit contain only privacy-safe evidence. |

### Provider-specific confirmation

Record these facts explicitly for a full live pass:

| Provider | Confirm |
|---|---|
| Slack | Socket Mode is connected; required bot/user scopes and slash commands are present; no Events API request URL is configured. |
| Telegram | `getUpdates` long polling receives the test event; `getWebhookInfo` shows no active webhook; only the configured private chat/user ID is admitted. |

## Operational health and recovery

Run this matrix whenever transport reliability, security material, queues, limits, or hosting changes.

| ID | Scenario | Expected result |
|---|---|---|
| O-01 | Inspect ChannelHost and Gateway health/readiness before and after an attachment is active. | Health identifies transport state, backlog/retry condition, credential/attachment state, certificate/delegation validity, and bounded failure reason without secret leakage. |
| O-02 | Simulate provider disconnect, rate limit, malformed payload, and transient remote failure. | Backoff and retry stay bounded; readiness and operator evidence distinguish recoverable from reconciliation-required work; no tight loop or unbounded queue occurs. |
| O-03 | Restart ChannelHost and Gateway independently with unacknowledged ingress, pending outbound work, and a pending approval. | Durable state replays/reconciles safely, acknowledgement remains correct, and no message, approval, or provider effect is duplicated. |
| O-04 | Rotate client/delegation material and pass the overlap deadline. | Valid current material works; previous works only within the documented bounded overlap; expired/unknown material is rejected and reported as revoked. |
| O-05 | Verify process and file isolation in the production installer/runtime path. | Gateway and ChannelHost run under separate principals; each can read only its own private keys; inventory, ownership, modes, and startup ordering fail closed on drift. |

## Required evidence

Attach or record:

- branch, commit, build mode, contract versions, operating system, and test account role;
- every scenario ID with Pass, Fail, Blocked, or Not Applicable;
- provider test-installation identifiers, redacted attachment/registration/conversation IDs, and explicit proof of the required transport;
- setup screenshots for prerequisites, validation failure, destination/target choice, active state, suspension/recovery, and operator health;
- safe extracts of ChannelHost/Gateway audit and operational logs, including restart/replay and failure cases;
- Narad and canonical-event evidence for channel messages, streams, approvals, and continuations;
- every defect found, its fix, and the scenario rerun after the fix;
- all skipped scenarios and why; and
- confirmation that disposable credentials and provider installations were revoked or retained intentionally after testing.

Use this completion summary:

```text
Channel verification scope: Automated | Full live
Branch/commit and contract version:
Build and stack:
Providers/transports tested:
Scenarios passed:
Scenarios failed:
Scenarios blocked/skipped:
Security and lifecycle evidence:
Ingress/outbound/restart evidence:
Setup UX evidence:
Provider-console and service-log findings:
Credentials/installations cleaned up:
Remaining risk:
```

Work is not done while an applicable scenario fails, a provider requires a webhook, a setup step requires hidden knowledge, authorization or delegation can be bypassed, a restart can duplicate an effect or resolution, or the reported scope overstates the evidence.
