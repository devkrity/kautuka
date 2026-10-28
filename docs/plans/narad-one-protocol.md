# Narad as one protocol (Kautuka-first, extractable)

**Status:** Active. Unbucketed remaining work: [signed-backlog.md](signed-backlog.md).

**Owner:** Product architecture

**Activation condition:** Sequential workflows and governed reviews are on `feature/simple-workflow`. This plan starts after HITL seam confusion is accepted: Telegram can notify a workflow pause while Approvals web and Narad do not share one wait.

**Debate:** platform architect; Fable 5.1 on HITL seams, libraries, and product name.

**Related:** [Agent Interaction Protocol](../reference/agent-interaction-protocol.md), [Kautuka Narad adoption](../reference/agent-interaction-protocol-kautuka-adoption.md), [agent-workflows.md](agent-workflows.md), [governed-workflow-platform.md](governed-workflow-platform.md), [ADR 0006](../adr/0006-ag-ui-interoperability-and-product-ux.md) (superseded AG-UI wire; the *intent* in §4 still holds).

## You are making sense

Narad is the **only developer interaction protocol** with core, both directions:

- **Out:** authority events. Clients reduce them. They never append them.
- **In:** the Narad **command catalog**. User intent (`resolve-interrupt`, `stop-active-root`, send message) is accepted or rejected by transport; accepted effects appear as domain events (spec §2 last paragraph). Commands are requests into the host-client coordination protocol; the protocol is not the server journal.

A Kautuka UI, a third-party client, and ChannelHost all invoke the same commands. ChannelHost is a **transport adapter** (Telegram `callback_query` → `resolve-interrupt`). It is not a second HITL protocol.

The reducer is stateless. Kautuka’s **server** owns durable event storage; Narad Protocol defines how the host coordinates that state with clients. Clients do **not** emit `interrupt.resolved`. Replay and catch-up follow from storing host events; they do not define Narad. Do not call the journal “hashed”: `NaradPersistedEvent` has no hash field, the SQLite store has none, the spec never requires one. Contiguous sequences and fail-closed replay are the proven properties. A hash chain is a later feature, not a claim.

## What is wrong today (proven)

| Door | Request | Resolve | Live Approvals doorbell |
|---|---|---|---|
| Chat / in-step tool | `interrupt.requested` on the agent session + SQL row | `POST /api/narad/v1/commands/resolve-interrupt` `{ interruptId, decision }` | Journal-derived `interrupt.*` / `run.paused` on `RuntimeEventStreamHub`, plus a Watch ping (`hitl.invalidation`) for mid-pack SQL updates that do not yet emit `interrupt.resolved` |
| Auto-Pilot pre-run | SQL row, no session (by design) | same command | Watch doorbell (`hitl.invalidation`). SQL is the inbox projection. No fake Auto-Pilot session and no `interrupt.requested` |
| Workflow step review | `interrupt.requested` + `workflow.paused` on the instance journal + SQL `kind=workflow_review` | same command; `WorkflowReviewResolvedHandler` then `ContinueAsync` | Journal-derived: owner-stamped `interrupt.requested` / `workflow.paused` reach `SubscribeForUserApprovals` |

`PATCH /api/approvals/{id}/resolve` is deleted. ChannelHost maps callback token → `IThreadCommandService.ResolveInterruptAsync`.

**Packed-card Stop live capture (deferred).** Covered by `ThreadCommandServiceTests.PackedStopRunClosesRemainingMembersAndPublishesInterruptResolved`, `NestedPackedStopRunPublishesRootStopRequested`, `BrowserPackedStopRunEnqueuesStopContinuation`, `StopActiveRootDeniesPendingApprovalsForTheRun`; `ApprovalContinuationDispatcherServiceTests.DispatcherRoutesStopRunThroughContinuationOrchestrator`; `AgentExecutionEventWriterTests.WriterPersistsLiveUserStopAsRunCancelledTerminal`. Expected live observation on My Assistant: one `run.cancelled` (never `run.finished`) and one `interrupt.resolved{decision: stop_run}`. No Playwright spec exists for it.

## Compete with AG-UI without copying it

[AG-UI](https://docs.ag-ui.com/introduction) (CopilotKit) is a bidirectional **runtime UI** protocol: stream `RUN_*`, `TEXT_MESSAGE_*`, `TOOL_CALL_*`, state snapshots to draw a chat. It is not a replayable journal with first-valid HITL, multi-day pause, and phone adapters.

Kautuka already chose Narad as the sole production wire (ADR 0006 superseded). The spin-off is **not** “become AG-UI.” It is:

- Replay from zero to identical **client** state (conformance traces).
- One open blocking wait, closed exactly once, expiry as a fact (`interrupt.resolved` `decision=expired`).
- Channels as ingress, not a second protocol.
- Command in, events out. AG-UI can sit *on* that journal later. It must not become the journal.

## Target shape (simple)

```
Chat / Approvals / 3rd-party client / ChannelHost
        │  Narad commands only  (interruptId + decision)
        │  resolve-interrupt | stop-active-root | message | …
        ▼
Narad HTTP binding  (or in-process IThreadCommandService used by ChannelHost)
        ▼
Gateway core  (eligibility, first-valid, expiry, continuation)
        │  append facts
        ▼
Narad Protocol coordinates the session-run and workflow-instance lanes between host and clients; Kautuka persists the underlying events
        │  attach / events / stream
        ▼
Stateless reducers  (workspace, Approvals list, workflow page, channel projector)
```

- **One wait fact:** `interrupt.requested` then a pause, same `waitId`. Intra-step: `run.paused` on the agent session. Inter-step: workflow-scope interrupt on the **instance journal** ([agent-workflows.md](agent-workflows.md): `scope: run | workflow`) plus existing `workflow.paused`. No fake orchestrator run. Do not hold the finished step run open unless an experiment proves MAF needs it.
- **One inbound command:** spec-shaped `resolve-interrupt { interruptId, decision, reason?, expectedRevision? }` on the owning session (agent session **or** workflow instance id). Map Kautuka `trustThisRun` / `stopRun` onto `actions` (`approve_and_trust`, `reject_and_stop`) or a Kautuka-profile extension — not a second HTTP API. Approvals UI posts this command (row already has `sessionId`). `PATCH /api/approvals/{id}/resolve` is gone. ChannelHost maps callback token → `IThreadCommandService.ResolveInterruptAsync`. Never resume from a channel.
- **One emission point:** `HitlApprovalRequested` is raised in the same function that appends `interrupt.requested`.
- **Inbox:** query over open interrupts (`tool_approval_requests` remains a Kautuka **projection**). `/api/approvals/changes` watches real `interrupt.*` / matching pause events on **one** hub, including the workflow instance session id. Session-less Auto-Pilot and mid-pack SQL updates use a Watch doorbell typed `hitl.invalidation`, not a Narad event name.

## Libraries (agreed with Fable)

The extract unit is **not** “TS client + .NET server this quarter.” The already-normative unit is the **spec package** (JSON schemas + conformance traces + prose). SDKs consume it.

**Ships in 90 days (Kautuka-first)**

1. Spec: add a normative **command catalog** (`resolve-interrupt`, `stop-active-root`, `message` + result vocabulary). HTTP binding is one recommended shape, non-normative per spec §10.2. `resolve-all-interrupts`, `resume-waiting-root`, `retry-target`, `cancel-graph-node` stay Kautuka-profile until a second host wants them.
2. Make `resolve-interrupt` spec-shaped; prove how `interruptId` joins to `tool_approval_requests` (unknown for packed HITL).
3. `@devkrity/narad`: delete stub `createNaradSessionIntents`; add `createCommandClient({ baseUrl, fetch })` that actually POSTs. `kautukaNaradIntents.ts` becomes a thin wrapper or is deleted. `@devkrity/narad-react` stays React glue. **In tree:** stub deleted; `createCommandClient` POSTs `resolve-interrupt`.
4. Split `Kautuka.Contracts/Narad` into `Narad.Contracts` (already has no `using Kautuka.*`). Gateway references it. Zero behaviour change.
5. .NET **lifecycle-subset** fold (`NaradRunHistoryState`: open interrupts, pause, open tools, open children) asserted against the **same** `.jsonl` traces the TS client uses. Full-state byte equality across languages is **not** a requirement (spec §13: client reduces; server proves invariants). **In tree:** `NaradRunHistoryStateConformanceTests`.

**Later (not this quarter):** `Narad.Server` (command DTOs, SSE writer, attach coordinator, fold helpers), gated on the trace harness and spec-shaped commands being live. Python only when a non-.NET host exists (a **server** adapter, not a second UI client). Hash chain only if implemented, never as marketing.

**Stays in Kautuka Gateway:** SQLite journal and sequence allocation, RBAC, expiry sweeper, approval-row projection, `ApprovalResolutionService`, `ContinueAsync` / MAF, Telegram chrome, `/agents/{agentId}` prefix, Auto-Pilot.

**Not this:** core “consumes events the client sent.” Core consumes **commands**. Projectors inside the host fold events with the lifecycle subset.

## Product name

`protocolVersion` `narad/v1` is fail-closed in the TS client, `NaradProtocolVersions`, and spec §13. Kautuka consumes `@devkrity/narad` / `@devkrity/narad-react` / `@devkrity/narad-spec` from `devkrity/narad` git tags. Host contracts stay `Narad.Contracts`. Reserve `@devkrity` on npm and the GitHub org before first publish.

| Candidate | Verdict |
|---|---|
| **Narad** | Signed product and wire. |
| Vritta (Fable signed) | **Reject.** [Vritti](https://www.npmjs.com/package/@vritti/api-sdk) is an existing AI/UI npm org; English speakers will type and search `vritti`. |
| Pramana | Occupied: agent claim-attestation protocol + `pramana-protocol` on npm. |
| Samvad | Occupied: [samvad.dev](https://www.samvad.dev/) A2A wire protocol. |
| Katha | Occupied: `@zaymonoid/katha` state/saga lib; also “story” SEO. |
| Varta | Occupied: Varta Lifeline Protocol (`@varta-health/client`). |
| Annal / Acta | Occupied: npm logger / React state manager. |
| Sthiti | Reject. `@sthira/core` is a frontend execution engine; one-letter trap. |
| Niyama | Reject. Close to SarthiAI `niyam-sdk` (agent capability gates). |
| Ledger / Docket / Interlock / Chronicle | Taken or name one feature only. |
| **Narad** under **Devkrity** (owner) | **Signed product name.** House is Devkrity; repo/package is `devkrity/narad`, not a top-level Narad brand. Nārada is the messenger between worlds — UI / channels / server. Speak: *NAH-rud*. Spell **Narad**, never Narada (that is [narada.ai](https://docs.narada.ai/documentation/installation)). Unscoped npm `narad` is taken; ignore it. Install line is unambiguous: `npm i @devkrity/narad`. |

**Signed:** product is **Narad**, house is **Devkrity**. GitHub product URL is `devkrity/narad` when that org exists; current private extract remote is `https://github.com/devkrity/narad`. npm `@devkrity/narad` (client), `@devkrity/narad-react`. NuGet `Devkrity.Narad` / `Devkrity.Narad.Contracts` (later `Devkrity.Narad.Server`). Wire id is `narad/v1`.

## Fable disagreements (both rounds)

1. Run-parked inter-step review (hold the finished step run open): spec-pure, unproven MAF. Kautuka-first keeps workflow-scope interrupt + `workflow.paused`.
2. **Agreed:** extract is spec + Contracts + TS command helpers; `.Server` is later; hashed journal is unproven; command body must become `interruptId`/`decision`; .NET fold is lifecycle-subset trace equivalence; ChannelHost bypass is the command layer, not a second resolver.
3. **Name:** Fable signed **Vritta**; rejected (Vritti). Owner signed **Narad** as `devkrity/narad`. In-tree packages are `@devkrity/narad` / `@devkrity/narad-react`.

## What stays out of the protocol

Actor eligibility and first-valid transactions (server policy behind the command). Expiry sweeper. `tool_approval_requests` columns. Telegram card chrome and callback tokens. MAF graph. Auto-Pilot scheduling.

## 90-day ship (Kautuka first, then extract)

1. **Prove the doorbell.** Create `workflow_review`; assert `interrupt.requested` on the instance journal; assert Approvals live-refresh without remount. **In tree:** owner-stamped `interrupt.requested` + `workflow.paused` publish onto `IRuntimeEventStreamHub`; hub test covers instance-session `workflow.paused`.
2. **Workflow-scope interrupt** on the same hub Approvals already watches; keep `workflow.paused`.
3. **Spec-shaped `resolve-interrupt`.** Approvals UI and ChannelHost (`IThreadCommandService`) call it. `PATCH /api/approvals/{id}/resolve` is deleted. `WorkflowReviewResolvedHandler` stays a consequence (`ContinueAsync`), not a second verb.
4. **Doorbell is not Narad-typed.** Auto-Pilot pre-run has no session by design; no fake session. `ApprovalStreamInvalidationHandler` pings `hitl.invalidation`.
5. **Spec command catalog + `@devkrity/narad` command transport + `Narad.Contracts` + .NET trace harness** as above. Source of truth is `https://github.com/devkrity/narad`. Kautuka consumes git tags (`v0.1.0-alpha.1`) via `git+https` + `path:`. Traces and schemas live in `@devkrity/narad-spec`. `.Server` later.

## Non-goals

CopilotKit / AG-UI compatibility layer. Moving Telegram into the protocol. Replacing the approval table this quarter. Holding finished step runs open. Quorum. Visual DAG. Hash chain. Python SDK.

## Signed

User intent enters through the Narad command catalog (`interruptId` + `decision`). Core accepts or rejects. Accepted effects appear as host-authoritative events and are shared with clients through Narad Protocol. Channels translate provider callbacks into the same command service; clients cannot write host-authoritative state. Kautuka persists those events, which can be replayed for recovery. Spec + TypeScript client live in `devkrity/narad`; Kautuka consumes git tags. `.Server` after traces. Wire id `narad/v1`. Product name **Narad** (`devkrity/narad`).
