# ADR 0005: Channel consumers share canonical runtime truth

**Status:** Accepted
**Date:** 2026-07-19

## Decision

Gateway's canonical runtime event ledger remains the only authority for run, wait, approval, continuation, cancellation, stop, failure, and terminal state. Browser, Narad, Approvals, Telegram, Slack, audit views, and future delivery surfaces consume that same committed history. No transport, projector, attachment component, or frontend may infer a decision, manufacture a terminal outcome, or resume an agent.

The first valid resolution of a wait is committed transactionally by Gateway. Approval, approval-for-run, denial, cancellation, stop, and expiry retain distinct canonical meanings. Duplicate submissions return the committed outcome; a conflicting late submission cannot change it. A continuation claim is durable and belongs to the exact root, invocation, tool call, child session, approval, wait, and wait generation. Each parent invocation receives exactly one terminal tool result.

`WaitId` is the wait-generation identity and is newly generated for every persisted pause. `ApprovalRequestId` is the immutable identity of one approval generation. A direct approval wait binds `CorrelationId` to that approval request ID, and durable uniqueness permits at most one active wait for the correlation. A continuation that pauses again makes the old wait historical; every later approval uses a new approval request ID and new wait ID. Reusing an approval ID with different payload or lineage fails closed. No parallel numeric generation field or migration layer is introduced.

`RunDeliveryCompleted` is a canonical runtime-output boundary; it is not evidence that any external provider accepted a message. Provider acknowledgement, retry, uncertain effect, and exhaustion are durable ChannelHost delivery facts. They may feed sanitized audit evidence but cannot cause a runtime transition.

## State transitions

| Current state | Committed input | Next state | Owner |
|---|---|---|---|
| New | run started | Running | Gateway runtime |
| Running | wait and approval request committed | Waiting | Gateway runtime |
| Waiting | approval, approval-for-run, denial, cancellation, or expiry committed | Resuming | Gateway runtime |
| Waiting | stop committed | Stopping | Gateway runtime |
| Resuming | continuation claim acquired | Running | Gateway runtime |
| Running | later wait committed | Waiting | Gateway runtime |
| Running | result committed | Completed | Gateway runtime |
| Running or Resuming | failure committed | Failed | Gateway runtime |
| Stopping | stop completion committed | Stopped | Gateway runtime |

Completed, Failed, and Stopped are terminal. Cancel cancels the pending tool and resumes the run with a canonical cancelled tool result; it is distinct from Stop Run and does not terminalize the run. Deny and expiry likewise resume toward their defined negative tool outcomes. A projector rejects illegal order, broken sequence, or mismatched lineage instead of repairing history. Event-version migration must preserve historical meaning and fail closed when it cannot.

## Consumer attachment

Consumer attachment is derived state. One generalized contract owns snapshot selection, durable consumer cursor, atomic snapshot-to-live handoff, deduplication, backpressure, reconnect, and terminal handoff. It can attach before a run, during any lifecycle, or after terminal completion. It never writes compensating runtime events.

Projection responsibilities remain explicit:

- sequence and lineage validation verifies that committed events form a legal path;
- a reducer reconstructs projection state, including pending waits and open lifecycles;
- consumer attachment records what a particular consumer has observed;
- Narad mapping translates approved projection inputs into standards-aligned wire events;
- snapshot generation builds reconnect state from the same reducer; and
- transport writers deliver mapped output without becoming runtime authorities.

Partial classes count as one conceptual owner for architecture ratchets. File splitting cannot hide growing responsibility.

## Chat continuity (root attach)

Browser chat is a peer Narad Protocol client, coordinated with ChannelHost (Telegram / Slack) and Approvals through the same host-authoritative event history. Chat remains the rich interactive composer for local turns and local approval decisions. It must not own root life after an Narad interrupt terminal.

| Concern | Owner | Mechanism |
|---|---|---|
| User sends a chat turn | Chat composer | New run delivery through Narad Protocol → host event history |
| User approves or denies in chat | Chat composer | Narad resume on a **new** delivery under the same `rootRunId` |
| User approves on Telegram / Approvals | Gateway + that surface | Resolve + durable continuation on the root |
| Open chat stays coherent after anyone continues the root | Chat as projector | **Attach** to the live or waiting root via connect |

Rules:

- An Narad delivery is a local turn / local approve envelope, not root life.
- `/changes` is invalidation only; it is not a second transcript.
- Attach-to-existing-root is the primary way open chat follows a live root after interrupt, on thread open when the root is live or waiting, and after external resolve.
- Do not infer authority from Narad wire-event count, delivery `runId` count, or “same visible transcript envelope.” One canonical event may produce several wire events; attach may reopen lifecycles that the snapshot already contains.

### Measurable continuity

When the latest session root is live (started, no root `run.finished` / `run.failed`) or waiting (pending interrupt / active wait):

1. Open chat must start an attach/connect delivery within **5 seconds** of the relevant `/changes` revision (or on thread open if already live/waiting).
2. Open chat must not present a synthetic idle/terminal “done” envelope unless the root is actually terminal.
3. After external resolve while chat is open: within **30 seconds**, chat must show either a real wait (HITL interrupt UI) or live progress (including tool cards that exist in the journal projection)—not a zombie `Running` tool with no wait and no progress.
4. Continuity must not key off “the original local delivery still owns the run.”

### Named code smell: `LocalRootOwnershipSuppressesExternalProgress`

Reviewers and architecture tests hunt this smell mechanically:

- Treating `locallyStartedRootRunIds` (or equivalent) as a reason to **skip** attach/reconnect when an external revision shows root progress.
- Suppressing `/changes`-driven connect because “we started this root,” after the Narad delivery for that root has already terminated (including interrupt).
- Any new settle/ownership heuristic that reintroduces dual ideas of “live.”

Allowed narrow exception (delivery-concurrency safety, not root ownership): while an Narad delivery is **actively streaming**, do not start a competing connect that would inject a second terminal envelope. Ask “is there an active Narad delivery, and is the root live/waiting?”—not “is this our locally started run?”

## Provider simulation and verification

The production ChannelHost workers must also run against standalone, loopback-only Telegram and Slack protocol simulators without an alternate execution path or weakened Kautuka authorization. One explicit transport profile is supported: `Official` selects product-owned, pinned provider endpoints; `Simulator` selects the provider protocol surfaces beneath one configured loopback simulator address. Simulator mode is admitted only in Development or Test and Production rejects it during startup. Arbitrary per-provider URL overrides, TLS bypasses, and authentication bypasses are not supported. The selected profile and sanitized endpoint identity are recorded in evidence.

Acceptance scenarios are typed declarative TypeScript executed by Playwright. A second JSON/YAML scenario language is not introduced. Pull requests use deterministic simulators without provider credentials. Release candidates require live-provider compatibility evidence from isolated provider environments. A missing live run is neither a simulator pass nor a live failure.

## Consequences

- Channel integration cannot create an alternate Narad or continuation path.
- Chat continuity follows root attach; local Narad delivery ownership is not a second idea of “live.”
- Browser and channel lifecycle parity is tested from one scenario catalogue.
- Per-consumer cursors do not pollute canonical runtime identity.
- Provider delivery evidence remains auditable without controlling agent execution.
- General configurable provider URLs, production TLS bypasses, inbound webhooks, and WhatsApp are outside this decision.
- Completion requires all M0 through M5 gates in the approved hardening backlog; a focused regression pass is insufficient.
