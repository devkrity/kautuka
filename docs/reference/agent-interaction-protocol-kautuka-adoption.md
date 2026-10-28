# Kautuka adoption of Narad

**Status:** Implementation guide  
**Normative protocol:** [Agent Interaction Protocol](agent-interaction-protocol.md)

This page tracks Kautuka-specific migration and the **domain → Narad map**. It does not change Narad semantics.

## Target architecture

```text
MAF / runtime work
  → Kautuka domain facts (ordered)
  → Narad Protocol host-client coordination state (same names / meanings)
  → wire + derived hydrate only where documented
  → @devkrity/narad: validate and reduce host updates (no invent, no forgive)
  → Web: dumb view of reduced state
```

- **Source of truth:** domain facts when committed (agent.db / runtime journal).
- **Narad:** the public vocabulary of those facts (event names aligned with historical Kautuka).
- **Not authority:** transport, attachment, checkpoints, graph hydrate, client soft-lifecycle recovery.
- If the UI must invent lifecycle to stay coherent, **emit is wrong** — fix the map, not the client.

---

## Domain layers

| Layer | Role | Examples |
|---|---|---|
| **A. Runtime internal stream** | Produce-loop / MAF glue | `GatewayChat*` produce events |
| **B. Durable control state** | Recovery, not conversation authority | Wait rows, AAAT invocation state, checkpoints |
| **C. Narad coordination protocol** | Host-authoritative lifecycle updates clients validate and reduce | `run.*`, `tool.*`, `interrupt.*`, `message.*`, … |
| **D. Derived hydrate** | Rebuilt from **C** only | `messages.snapshot`, `graph.snapshot` / `graph.delta` |
| **E. Watch / control** | Cursors, capabilities | `session.revision`, control frames |

**A → C** mapping lives primarily in `AgentExecutionEventWriter` (plus early run lifecycle `PublishNaradAsync` for start/user/deny paths).

### Emit ownership (no dual invent)

| Path | May publish | Must not invent |
|---|---|---|
| `AgentExecutionEventWriter` | **Sole** domain → Narad map (produce loop + early lifecycle via `MapAsync` / `EmitDomainAsync`) | `run.paused` from StreamDone; `run.finished` for child wait; empty `waitId` resume |
| Early stream domain facts | `GatewayChatRunStarted`, `GatewayChatUserMessageRecorded`, `GatewayChatContinuationResumed`, `GatewayChatMessageTextStarted`, deny/readiness domain terminals | Direct `PublishNaradAsync` invent (removed) |
| `RunStopCommandService` | `run.stop.requested` | Terminals (produce maps cancel) |
| Approval resolution | `interrupt.resolved` | `run.resumed` (resume is stream re-entry via durable continuation enqueue, not invented lifecycle) |
| Consumer stream attach | Fold full session for graph hydrate, then live SSE | Deserializing Narad `run.finished` as legacy `GatewayChatStreamDone` (empty `TargetType` kills channel ingress) |
| Graph projection | Deterministic `graph.snapshot` / `graph.delta` | Index-only status patches that diverge from client node order; parented `JsonNode` reuse across serializations |
| Channel projection | Surface-agent session journals only; HITL one-shot finals on `run.finished`; mirror one-shot on `assistant.message.completed`; keep `attachmentIds` on projected user messages even when the caption is empty | AAAT parent-lane `sourceEventId` / nested `parent*` / `agent_tool` sessions to Telegram; channel-admit `sourceChannel` (and originator fallback) user echoes; nested journals with mismatched surface `agentId`; HITL pause-time `assistant.message.completed` stealing `agent_response` (translator now drains that event without emitting) |
| AAAT negative parent tool close | `tool.call.finished` with id `aaat:{id}:tool-terminal` | Parent `run.finished` as a substitute for child deny alone without domain parent end |
| Reconciliation / recovery | `tool.call.finished` / `run.error` with fixed recovery event ids; `trace.event` for quarantine | Child wait as parent pause; free-form non-Narad type names as authority lifecycle |

Live graph hydrate must emit for the **same root** the projector advanced (including AAAT parent-lane rows). Skipping hydrate after a journal apply desyncs `baseRevision` and causes `Graph revision gap requires resync` on every later delta.

Process-restart recovery **does** close open tools / child runs with recovery-coded terminals — that is an explicit domain recovery fact, not live produce invent.

---

## Domain stream (A) → Narad authority (C)

| Domain event | Domain meaning | Narad (authority) | Notes |
|---|---|---|---|
| Intent / user turn accepted | User message for session | `message.user.recorded` | Often direct publish with `run.started`. Optional `attachmentIds`; `content` may be empty when ids are present (workspace file-only send and channel inbound files). Bytes are REST, not Narad `attachment.*`. |
| Root/nested run begins | Execution starts | `run.started` | Nested: `parentRunId`, `parentToolCallId` |
| `GatewayChatTokenDelta` (`IsThinking=false`) | Assistant text | `message.text.started` (first), `message.text.delta`, later `message.text.completed` | Close on boundary / terminal |
| `GatewayChatTokenDelta` (`IsThinking=true`) | Reasoning | `reasoning.started`, `reasoning.delta`, `reasoning.completed` | `reasoningId` = `reasoning:{messageId}`; same open/close pairing as text |
| `GatewayChatToolCall` | Tool begins | `tool.call.started` | Full `args`, `executor` |
| `GatewayChatToolResult` | Tool success | `tool.call.finished` (`outcome=success`) | |
| `GatewayChatToolFailure` | Tool failure | `tool.call.finished` (`outcome=error`) | Sanitized `code` / `message` |
| `GatewayChatApprovalRequest` | **This run** pauses for HITL | `interrupt.requested` then `run.paused` | Only if domain pauses this run |
| `GatewayChatContinuationResumed` | **This run** resumes that wait | `run.resumed` | Only with matching open pause |
| `GatewayChatStreamDone` (true terminal phases) | Run ends | `run.finished` / `run.error` / `run.cancelled` | From `GatewayRunState.Phase`. Terminal map may stamp optional `response` on `run.finished` from domain result text so channel hitl mirrors do not depend only on delta buffers. |
| `GatewayChatWorkflowEvent` | User-visible runtime activity | `progress.recorded` | `category=runtime` |
| User stop | Cancel | `run.stop.requested` then `run.cancelled` | Stop command path |

### Internal-only (no Narad authority)

| Domain / stream | Disposition |
|---|---|
| `GatewayChatContinuationCheckpoint` | Recovery; adoption drop list / optional `trace.*` |
| `GatewayChatSessionReady` | Not conversation authority |
| `GatewayChatRunPhase` | Prefer `progress.recorded` at source, or drop |
| `GatewayChatAgentTransition` | Internal routing |
| `GatewayChatProduceBoundarySuspended` | Produce loop ends while domain run **still running** (e.g. AAAT child wait). Closes open assistant message fragments only; **emits no `run.*` lifecycle**. Durable truth is wait/invocation state (B), not a run terminal. |
| `GatewayChatStreamDone` with `phase=executing` + `waitStateReason=child` | **Forbidden.** Not a terminal; do not map to `run.finished`. |

### Durable state (B) rules (not a second event language)

| Durable row | Wire rule |
|---|---|
| Wait reason **approval** | Must already journal `interrupt.requested` + `run.paused` for that run |
| Packed HITL (Kautuka profile) | One `interrupt.requested`. Each `input.members[].requestId` is a valid `interruptId`. Posting the pack-head id alone resolves the head row only; `interrupt.resolved` waits until no human sibling is still open. |
| Wait reason **child** | Parent has **no** `run.paused` / `run.resumed` / terminal; child session owns pause/resume; parent keeps open `tool.call.started` for `invoke_agent_*` |
| Invocation `waiting_on_child` | Open parent tool until child completes and tool finishes |
| Checkpoint rows | Not session authority |

---

## Nested AAAT wire matrix

```text
Parent session authority:
  run.started (parent)
  … tool.call.started(invoke_agent_*)     # stays open through child HITL
  [optional] parent-session projections of child facts (sourceEventId / lineage)
  … tool.call.finished(invoke_agent_*)
  run.finished

Child session authority:
  run.started (child, parentToolCallId=…)
  interrupt.requested + run.paused          # if nested HITL
  run.resumed
  …
  run.finished
```

| Domain situation | Parent Narad | Child Narad |
|---|---|---|
| Child needs approval | Wait `child` + invocation `waiting_on_child` only | `interrupt.*` + `run.paused` |
| Approve / continue child | **No** parent `run.resumed` | child `run.resumed` + more work |
| Parent produce ends while waiting on child | Close open assistant fragments if needed; **no** run lifecycle | unchanged |

Parent conversation projections of child facts must still be **true Narad facts** (optional product visibility), never parent lifecycle impersonation (`run.resumed` / wait-terminal on parent `runId`).

---

## Lifecycle rules (domain = Narad client guards)

1. `run.started` once per `runId`.
2. Terminal once (`finished` | `error` | `cancelled`); only from **running** (resume first if paused).
3. `run.paused` only from **running**; open `waitId`.
4. `run.resumed` only from **paused** with matching `waitId`.
5. Tool: start once; finish once while run running.
6. No parent/run terminal while domain still has open tools, open interrupt, or **child wait**.
7. Session authority total-ordered by `sessionSequence`.
8. Run-tree `sequence` is per root journal tree — not a substitute for session order.

---

## Historical mapping (rename cutover)

| Historical Kautuka event | Narad |
|---|---|
| `run.started` | `run.started` |
| `run.paused` | `run.paused` |
| `run.resumed` | `run.resumed` |
| `run.stop.requested` | `run.stop.requested` |
| `run.finished` | `run.finished` (+ optional `usage`) |
| `run.failed` | `run.error` (+ optional partial `usage`) |
| `InputTokenCount` / `OutputTokenCount` on terminal usage | `usage.promptTokens` / `usage.completionTokens` (optional `usage.totalTokens`) |
| tool approval requested | `interrupt.requested` with approval `kind` |
| tool approval resolved | `interrupt.resolved` |
| tool call started | `tool.call.started` with complete `args` |
| tool completed/failed/cancelled | `tool.call.finished` with `outcome` and `response` |
| user message recorded | `message.user.recorded` |
| assistant text stream | `message.text.*` |
| assistant reasoning stream | `reasoning.*` |
| thread change watermark | `session.revision` |
| `run.phase.changed` | `progress.recorded` (`category=phase`) |
| `agent.session.ready` | `progress.recorded` (`category=session`) |
| `connector.invocation` | `progress.recorded` (`category=connector`) |
| `manager.message` | `progress.recorded` (`category=manager`) |
| `kautuka.execution_graph.v1` snapshot/delta | `graph.snapshot` / `graph.delta` hydrate (**not** authority) |
| `credential.host.check` | `trace.event` |
| unscoped custom/raw | scoped `session|workflow|run|node` custom/raw |

Delivery completion, provider acknowledgement, internal worker heartbeat, and transport retry are not Narad authority events.

---

## Drop list

| Historical Kautuka event | Disposition |
|---|---|
| `run.delivery.completed` | Derived consumer/attachment state |
| `run.heartbeat` | Server/client heartbeat controls (Narad §11) |
| `command.*` | Request APIs produce domain events |
| `agent.invocation.started` / `agent.invocation.completed` | Nested `run.started` + `parentToolCallId` |
| `checkpoint.created` | Backend recovery; `trace.*` if observability |
| Provider delivery acknowledgements, retries, rate limits | ChannelHost only |
| Consumer attachment cursors and snapshot boundaries | Derived consumer state |

Diagnostics for authorized operators: `trace.*` / gated `debug.*`. User-visible activity: `progress.recorded`. Execution topology: **derived** `graph.*` hydrate from authority — never persisted graph authority.

---

## Derived hydrate (D)

| Record | Source | Rule |
|---|---|---|
| `messages.snapshot` | Fold authority message events | Attach / resync only |
| `graph.snapshot` / `graph.delta` | Deterministic fold of authority run/tool/interrupt | Rebuildable from journal alone; attach folds **full session through boundary** so revision rails match authority history |

---

## Required implementation changes

1. Add `sessionSequence` to all session authority events.
2. Add `workflowSequence` to every event belonging to a workflow.
3. Preserve root-run `sequence` for each run tree.
4. Bind an optional `clientExecutorId` on `run.started`.
5. Make scheduled execution select its client executor explicitly when client tools are enabled.
6. Replace tool catalog publication with backend-owned tool configuration.
7. Separate targeted `client.tool.execute` controls from authority streams.
8. Add client executor leases and bidirectional server/client heartbeat controls.
9. Remove `run.heartbeat` from authority history.
10. Remove generic `command.*` events from core; request APIs produce domain events.
11. Remove `agent.invocation.*`; nested `run.started` plus `parentToolCallId` represents agent-as-tool lineage.
12. Enforce no run terminal while child runs, tools, interrupts, or pause cycles remain open.
13. Implement session-sequence history and gap-free live attachment.
14. Adopt the profile negotiation and snapshot boundaries from the specification.
15. Project user-visible activity to `progress.recorded`; derive execution topology as run-scoped `graph.*` hydrate records from authority, never as persisted graph authority.
16. Stamp optional `usage` on completed public `agent.turn` `progress.recorded` facts (unique `progressId` per generation, including HITL resume). Chat/trace sum those events. `run.finished.usage` is that invoke only. Persist one usage-db fact per turn with a unique fact `run_id`.
17. **Single pure map** domain stream → Narad: no inventing pause/resume/terminal facts the domain did not commit (especially AAAT parent child-wait).
18. Journal shape tests must assert **event types and open/close rules**, not browser silence.

---

## Compatibility window

Narad cutover is forward-only. There is no compatibility adapter, migration flag, or dual-read path in the browser. Reset development databases and browser storage after upgrading (see [Frontend architecture](../architecture/frontend.md#destructive-upgrade) and [Troubleshooting](../operations/troubleshooting.md#narad-cutover-reset)).

---

## Reference clients

- `@devkrity/narad`: reducer, replay, attachment, lease, and targeted execution  
- `@devkrity/narad-react`: React bindings and presentation helpers  

Kautuka should dogfood those packages before Narad v1 is frozen.

---

## Deterministic journal tests

Integration tests under `NaradDomainEventMapJournalTests` drive `AgentExecutionEventWriter` with domain stream events and a **scripted Ollama-shaped `IChatClient`** seed (tool names / args / assistant text grounded in captured parent-session shape for “roll a dN”, not live model sampling).

Assertions: Narad type sequences and AAAT parent/child invariants from this document — not random model output.
