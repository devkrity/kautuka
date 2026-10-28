# ADR 0006: AG-UI interoperability boundary and one product UX

**Status:** Superseded (2026-08-06) — Narad is the sole production wire protocol; AG-UI client packages, conformance examples, and `/api/agent-runtime` browser projection have been removed. Retained for historical rationale only.
**Date:** 2026-08-04

## Context

Kautuka already treats Gateway's canonical runtime event journal as the only authority for run-visible state, approvals, continuations, and terminal outcomes ([ADR 0002](0002-canonical-runtime-events.md), [ADR 0005](0005-channel-runtime-projection-and-verification.md)). Gateway projects that journal to standards-shaped AG-UI SSE at `/api/agent-runtime`; browser chat, Approvals, and ChannelHost are peer consumers of the same truth.

The product ships one browser chat workspace (`AgentPageV2` at `/workspace`) over the same journal. Third-party AG-UI clients can connect to the same endpoint. Kautuka's execution model includes nested agent-as-a-tool (AAAT), parallel child work, durable waits, and channel-resolved approvals—capabilities that exceed what a generic AG-UI client can represent without Kautuka-specific projection and rendering.

This ADR records the product architecture decision for how AG-UI, client frameworks, and complementary standards related to Kautuka's workflow model before Narad cutover. Current wire semantics are in [Agent Interaction Protocol](../reference/agent-interaction-protocol.md) and [Kautuka adoption of Narad](../reference/agent-interaction-protocol-kautuka-adoption.md).

## Decision

### 1. AG-UI is an external interoperability protocol

AG-UI is Kautuka's **external interoperability protocol** for agent-user interaction streaming. It is **not** Kautuka's product model, internal orchestration model, or workflow definition language. Workflow meaning, legal state transitions, and durable continuation belong to Gateway's canonical runtime domain—not to any client library's chat or routing model.

### 2. Backend owns workflow truth

Gateway owns workflow **meaning**, **state**, **transitions**, and **canonical truth**. Clients—including browser chat, channel surfaces, and third-party AG-UI consumers—derive observable state by reducing committed history and mapped wire events. They do not infer approvals, resume runs, manufacture terminal outcomes, or reconcile competing ideas of "live."

### 3. Backend projects AG-UI core plus versioned Kautuka extensions

Gateway remained the sole AG-UI projector. It emitted **standards-compliant AG-UI** for semantics the protocol already covered (for example `RUN_*`, `TEXT_MESSAGE_*`, `TOOL_CALL_*`, `STEP_*`, state and activity snapshots/deltas, and interrupt/resume envelopes).

Where Kautuka's internal execution graph, nested lineage, or HITL facts cannot be represented faithfully with generic AG-UI alone, Gateway adds **versioned Kautuka extensions** through existing extension mechanisms already in the codebase (`CUSTOM`, versioned `ACTIVITY_SNAPSHOT` / `ACTIVITY_DELTA` payloads such as `kautuka.hitl.resolved`, and versioned tool-result envelopes). Extensions are additive; conforming clients may ignore or generically render them while remaining correct on the core stream.

### 4. Client reduces events; user actions are explicit intents

Clients **reduce** AG-UI events into **deterministic presentation state** for rendering and interaction. User actions are **explicit intents** sent back to Gateway—run, connect/attach, stop, approve, deny, cancel, and other atomic commands—not implicit side effects of local UI state machines.

Presentation state includes layout, selection, optimistic local composition where the protocol allows it, and derived views of in-flight tool or activity UI. It excludes orchestration authority.

### 5. One Kautuka production UX; third-party clients are conformance references

Kautuka ships **one production interaction UX**: the Kautuka conversation and workflow workspace.

CopilotKit and assistant-ui remain valuable **conformance references** and interoperability targets. They are not maintained as a second Kautuka product surface. Small development examples and automated contract tests may prove that a third-party AG-UI client can connect, stream messages, render generic tools, and complete a standard interrupt without adopting Kautuka's full workflow presentation.

### 6. Dogfood a workflow-first client framework; productize later

Kautuka builds and dogfoods a **workflow-first client framework** on top of official AG-UI client foundations (event reduction, reconnect, and standards-shaped state). The framework:

- works with any backend that satisfies the AG-UI core compatibility profile;
- progressively enables richer workflow capabilities when a backend advertises a versioned Kautuka profile; and
- may later be extracted as a supported developer SDK (headless client, React primitives, workflow components, schemas, and examples).

"Client framework" means protocol reduction, extension handling, rendering primitives, interaction APIs, adapters, and an optional styled workspace. It does **not** mean a second orchestration runtime.

### 7. No browser-owned orchestration runtime

The browser must not host an orchestration runtime that owns workflow transitions, continuation claims, approval resolution, or terminal run outcomes. Local abort, stream teardown, or UI-level deferral may exist only as presentation or transport mechanics after Gateway has accepted or rejected the durable command.

This extends existing chat-continuity rules: root attach follows canonical journal revision; AG-UI run/resume is a local turn or local approve envelope, not root life ([ADR 0005](0005-channel-runtime-projection-and-verification.md)).

### 8. Complementary standards at their boundaries

Kautuka composes open standards at their intended boundaries rather than selecting one as a universal internal model:

| Standard | Role in Kautuka |
|---|---|
| **AG-UI** | User-interaction streaming and third-party client interoperability |
| **A2A** | External agent federation and independently addressable long-running tasks (future boundary; not a replacement for AG-UI chat projection) |
| **A2UI** | Optional generative UI artifacts carried over AG-UI or A2A (future; describes UI components, not execution orchestration) |
| **MCP** | Tools and context at execution time ([Extension guide](../development/extensions.md)) |
| **OpenTelemetry** | Cross-service observability ([Security model](../architecture/security.md), shared telemetry wiring) |

Canonical execution graph semantics remain Kautuka's internal domain model. AG-UI is the primary user-interaction projection; MCP supplies tool/context surfaces; observability is operational evidence, not workflow authority.

## Evidence

Code and documentation that implement or constrain this decision today:

- Canonical runtime events and projection: `RuntimeEventPublisher`, `CanonicalRuntimeThreadProjector`, `CanonicalRuntimeEventProjector`, `AgentRuntimeEventStreamWriter` under `src/backend/Kautuka.Gateway/RuntimePlane`.
- AG-UI endpoint surface: `/api/agent-runtime` mappings in `src/backend/Kautuka.Gateway/Endpoints`.
- Documented canonical-to-AG-UI mapping and identifier model (removed with Narad cutover; see [Kautuka adoption of Narad](../reference/agent-interaction-protocol-kautuka-adoption.md)).
- Versioned Kautuka wire extensions already projected: `kautuka.hitl.resolved` activity, versioned tool failure/cancellation envelopes, nested facts via `CUSTOM`.
- Peer consumer model and chat continuity guardrails: [ADR 0005](0005-channel-runtime-projection-and-verification.md), [Frontend architecture](../architecture/frontend.md), [Channel runtime ownership](../reference/channel-runtime-ownership.md).
- MCP and Skills execution boundaries: [Extension guide](../development/extensions.md).
- OpenTelemetry/logging wiring: `src/shared/Kautuka.Telemetry`.

## Rejected alternatives

### AG-UI as the internal orchestration model

Treating AG-UI event types, client reducers, or third-party chat frameworks as the source of workflow truth would duplicate Gateway's canonical journal, break channel and approval parity, and force compatibility repairs when the protocol cannot represent concurrent nested execution faithfully. Rejected.

### Maintaining CopilotKit (or assistant-ui) as a co-equal product UX

Preserving a second fully featured product surface constrains Kautuka's workflow presentation to the least-capable client, duplicates attach/continuity logic, and splits product investment. Third-party clients remain contract-test and reference targets only. Rejected for production UX.

### Browser-owned orchestration and continuation

Client-side resume graphs, inferred approval outcomes, or transcript reconciliation from raw SSE would reintroduce dual authority and the `LocalRootOwnershipSuppressesExternalProgress` failure mode. Rejected.

### Replacing AG-UI with A2A or ACP for browser chat

A2A and ACP address federation and IDE-oriented client semantics respectively; neither replaces AG-UI's implemented browser streaming, tool-call UI, and interrupt model in this repository. Rejected as a wholesale swap; A2A remains a complementary external boundary.

### Inventing parallel custom wire events for standardized AG-UI semantics

Duplicating `RUN_*`, `TOOL_CALL_*`, interrupt, or activity semantics with bespoke events would break third-party conformance and fragment projection. Kautuka extensions belong in versioned envelopes only where AG-UI core is insufficient. Rejected.

## Guardrails

- **Single projector:** only Gateway maps canonical events to AG-UI; clients and ChannelHost do not emit AG-UI directly.
- **Journal-first reconnect:** presentation state rebuilds from canonical history and approved snapshots; client caches are not authoritative after reconnect.
- **Atomic intents:** approve-all, stop, resume, retry, and external-resolution sequencing move behind durable Gateway commands; the browser sequences presentation around command outcomes rather than owning workflow transitions.
- **No dual live:** open chat attaches to live or waiting roots via revision invalidation; local AG-UI delivery ownership must not suppress external progress ([ADR 0005](0005-channel-runtime-projection-and-verification.md)).
- **Extension discipline:** new Kautuka-specific facts use versioned activity or `CUSTOM` payloads; do not fork standardized AG-UI event names.
- **Conformance without product duplication:** CopilotKit/assistant-ui examples and tests prove interoperability; they do not define navigation, onboarding, or workflow workspace product requirements.
- **Ratchet compliance:** growing client or projector authority requires an explicit ADR per [Auditability decisions](../architecture/auditability-decisions.md).

## Consequences

- Third-party AG-UI clients can integrate against a documented core profile without seeing Kautuka's full execution graph; rich workflow UI requires the Kautuka client profile.
- Gateway projection work becomes the compatibility gate: core degradation and Kautuka extensions must be testable from one canonical journal.
- Current multi-surface browser chat is transitional; production UX consolidation and client-framework extraction proceed without granting either surface orchestration authority.
- Channel, Approvals, audit, and browser chat remain peers—this decision does not introduce a browser-specific continuation path.
- Future A2A federation and optional A2UI rendering attach at documented boundaries and do not relax backend ownership of workflow truth.
- Documentation for protocol profiles, client-core implementation, and SDK productization belonged in follow-on work; Narad superseded the AG-UI client framework path.
