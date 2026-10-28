# Agent Interaction Protocol (Narad)

**Status:** 1.0 Release Candidate 1  
**Protocol id:** `narad/v1`  
**Wire naming:** `dotted.lowercase`  
**Normative schemas:** [`@devkrity/narad-spec` schema](https://github.com/devkrity/narad/tree/v0.1.0-alpha.1/packages/narad-spec/schema)  
**Conformance traces:** [`@devkrity/narad-spec` conformance](https://github.com/devkrity/narad/tree/v0.1.0-alpha.1/packages/narad-spec/conformance)

Narad is a transport-neutral protocol for durable agent sessions, runs, workflows, tools, interrupts, conversation, replay, and client execution. It defines observable facts and reducer behavior. It does not prescribe scheduler internals, model providers, storage engines, HTTP routes, or UI frameworks.

This document is normative. Design rationale is separate in [Narad rationale](agent-interaction-protocol-rationale.md). Product-specific adoption belongs in an implementation guide such as [Kautuka Narad adoption](agent-interaction-protocol-kautuka-adoption.md).

---

## 1. Normative language

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHOULD**, **SHOULD NOT**, and **MAY** are interpreted as described by RFC 2119 and RFC 8174 when they appear in uppercase.

## 2. Design model

Narad separates four kinds of wire data:

1. **Authority events** are immutable facts appended by the backend. They reconstruct durable session state.
2. **Watch events** are scoped revision notifications. They indicate that authority changed but do not replace it.
3. **Hydrate records** are non-journal derived projections anchored to authority cursors. They include transcript snapshots and deterministic graph state under negotiated profiles.
4. **Control messages** are non-journal transport traffic for attachment, heartbeat, and targeted client-tool execution.

Only authority events determine run, workflow, interrupt, tool, and transcript state. Receiving an authority event **MUST NOT** itself trigger a client-side side effect. Client-side tools execute only from targeted control messages defined in §11.

### 2.1 Protocol profiles

The exact core event set is:

- `run.started`
- `run.paused`
- `run.resumed`
- `run.stop.requested`
- `run.finished`
- `run.error`
- `run.cancelled`
- `interrupt.requested`
- `interrupt.resolved`
- `tool.call.started`
- `tool.call.finished`
- `session.revision`
- `run.revision`

Profiles add the following types:

| Profile | Id | Types |
|---|---|---|
| Workflow | `narad.workflow/v1` | `workflow.*`, `workflow.revision` |
| Conversation | `narad.conversation/v1` | `message.*`, `reasoning.*`, `messages.snapshot` |
| Attachments | `narad.attachments/v1` | `attachment.*` |
| Artifacts | `narad.artifacts/v1` | `artifact.*` |
| State | `narad.state/v1` | `state.snapshot`, `state.delta`, `state.compacted` |
| UI | `narad.ui/v1` | `ui.state.*`, `view.*` |
| Graph | `narad.graph/v1` | `graph.snapshot`, `graph.delta`, `node.revision` |
| Progress | `narad.progress/v1` | `progress.recorded` |
| Workspace watch | `narad.workspace/v1` | `workspace.revision`, `session.created`, `session.updated`, `session.archived` |
| Extensions | `narad.extensions/v1` | scoped `*.custom` and `*.raw` |
| Trace | `narad.trace/v1` | `trace.*` |
| Debug | `narad.debug/v1` | `debug.*` |

Servers **MUST** advertise active profiles before sending profile events. Clients **MUST** ignore events from profiles they did not negotiate. A client claiming a profile **MUST** validate that profile’s authority events.

Commands and request APIs are not Narad authority events. A client request is accepted or rejected by its transport API; accepted effects appear as Narad domain events. Implementations that require durable asynchronous operation progress may define a separate profile.

### 2.2 Command catalog

Commands are the inbound counterpart of authority events. They are not journal facts. A host **MUST** accept or reject a command before mutating durable state; accepted effects **MUST** appear as authority events.

Normative command names and bodies:

| Command | Body | Accepted effects |
|---|---|---|
| `resolve-interrupt` | `interruptId` (required), `decision` (required; one of the matching `interrupt.requested` `actions`, or `expired` for backend expiry), `reason?`, `expectedRevision?` | `interrupt.resolved`, then `run.resumed` / a run terminal, or `workflow.resumed` / a workflow terminal |
| `stop-active-root` | host-defined | `run.stop.requested` and subsequent terminals |
| `message` | host-defined user message | `message.user.recorded` and subsequent run events |

HTTP is one recommended binding, not the protocol (see §10.2). A recommended `resolve-interrupt` binding is:

```http
POST /api/narad/v1/commands/resolve-interrupt
```

`interruptId` identifies the open interrupt. The server **MUST** authorize the caller against the owning journal (agent session or workflow instance) and **MUST NOT** require the caller to repeat `agentId` / `sessionId` in the URL when `interruptId` already resolves to that owner.

---

## 3. Entities and concurrency

### 3.1 Session

A **session** (`sessionId`) is the durable conversation and ordering boundary.

A session has at most one active top-level execution:

- one independent top-level run; or
- one active workflow containing its active top-level run.

These alternatives share the same slot. A conflicting start request **MUST** be rejected or queued by the request API; it **MUST NOT** create a second active top-level execution.

### 3.2 Workflow

A **workflow** (`workflowId`) is optional orchestration containing one or more run trees. It has its own total ordering field, `workflowSequence`. At most one top-level run inside a workflow may be active at once. Nested runs may execute concurrently.

### 3.3 Run

A **run** (`runId`) is one execution instance. A top-level run begins a run journal identified by `rootRunId`; nested runs retain their parent lineage’s `rootRunId`.

A run may optionally bind one immutable logical client execution endpoint:

```json
"clientExecutorId": "desktop-worker-123"
```

The backend selects that endpoint before appending `run.started`. Selection policy is implementation-defined. Scheduled execution that requires client tools **MUST** select an endpoint explicitly. If no endpoint is selected, the backend **MUST NOT** start a tool call with `executor=client`.

Backend selection for a nested run defaults to the parent’s executor, but the nested run **MUST** record its effective `clientExecutorId` explicitly in `run.started`; omission means no client executor. Once a run starts, its binding **MUST NOT** change.

### 3.4 Interrupt

An **interrupt** (`interruptId`) is an external decision or input request associated with one paused run and one pause cycle (`waitId`).

Narad v1 permits at most one open blocking interrupt per run. Nested runs may each have their own interrupt.

### 3.5 Tool call

A **tool call** (`toolCallId`) is an action belonging to exactly one run. Its executor is either:

- `backend`: executed by the backend; or
- `client`: executed by the run’s bound `clientExecutorId`.

Tool availability, registration, model tool definitions, and catalog management are backend concerns. Narad has no core tool-catalog event.

---

## 4. Identity and ordering

### 4.1 Event identity

Every authority or watch event has an `eventId`. The canonical deduplication key is:

```text
(sessionId, eventId)
```

For workspace-watch events it is:

```text
(workspaceId, eventId)
```

An issuer **MUST NOT** reuse an `eventId` within that scope. Replayed delivery of the same fact **MUST** preserve the same `eventId`.

### 4.2 Ordering domains

Narad defines three authority orderings:

| Ordering | Field | Scope | Requirement |
|---|---|---|---|
| Session | `sessionSequence` | `sessionId` | Strictly increasing and contiguous across all session authority events |
| Workflow | `workflowSequence` | `workflowId` | Strictly increasing and contiguous across all authority events belonging to that workflow |
| Run tree | `sequence` | `rootRunId` | Strictly increasing and contiguous across all authority events belonging to that run tree |

An event may participate in more than one ordering:

- Every session authority event has `sessionSequence`.
- Every workflow authority event, including run events belonging to that workflow, also has `workflowId` and `workflowSequence`.
- Every run-tree authority event has `rootRunId`, `runId`, and `sequence`.

The backend assigns all applicable sequence values atomically with the event append. Timestamps are informational and **MUST NOT** be used to reorder authority.

Watch events have a monotonic `revision` within their scope and do not consume authority sequences. Coalescing may skip observable revision values but revisions **MUST NOT** regress.

---

## 5. Event envelopes

Every authority and watch event is a JSON object with:

| Field | Type | Requirement |
|---|---|---|
| `type` | string | Required `dotted.lowercase` event type |
| `protocolVersion` | string | Required; `narad/v1` |
| `eventId` | string | Required stable identity |
| `timestamp` | string | Required ISO-8601 UTC timestamp |

### 5.1 Required fields by class

| Event class | Additional required fields |
|---|---|
| Session authority | `sessionId`, `sessionSequence` |
| Workflow authority | session authority fields + `workflowId`, `workflowSequence` |
| Run-tree authority | session authority fields + `rootRunId`, `runId`, `sequence`; workflow fields when applicable |
| Session watch | `sessionId`, `revision` |
| Workflow watch | `sessionId`, `workflowId`, `revision` |
| Run watch | `sessionId`, `rootRunId`, `runId`, `revision` |
| Node watch | `sessionId`, `workflowId`, `nodeId`, `revision` |
| Workspace watch | `workspaceId`, `revision` |

Per-type fields in §§7–10 are additional requirements. Unknown optional fields **MUST** be ignored.

Operational core fields, including tool arguments and tool responses, **MUST NOT** be removed from an event delivered to a principal authorized to consume that authority stream. Conversation-display redaction is defined separately in §9.4.

---

## 6. Run and workflow state machines

### 6.1 Run states

A run is in exactly one of:

- `running`
- `paused`
- `finished`
- `error`
- `cancelled`

`stopRequested` is an orthogonal flag, not another state.

Valid transitions are:

```text
none      -> running       run.started
running   -> paused        run.paused
paused    -> running       run.resumed
running   -> terminal      run.finished | run.error | run.cancelled
```

A paused run **MUST** resume before emitting a terminal.

### 6.2 Open-work invariant

A run **MUST NOT** emit any terminal while it owns any open:

- child run;
- tool call;
- interrupt;
- pause cycle; or
- profile-defined blocking aggregate.

Backends must close, cancel, or fail owned work first. Handling timeouts, unavailable workers, and recovery attempts is implementation-defined; the resulting authority events are not.

### 6.3 Stop

`run.stop.requested` records a request to stop and sets `stopRequested=true`. After it:

- the backend **MUST NOT** start new child runs or tool calls for the target run;
- existing work is closed according to backend policy;
- the run eventually emits `run.cancelled`, `run.error`, or, if work had already completed successfully, `run.finished`.

Silence or transport disconnection **MUST NOT** be interpreted as a terminal.

### 6.4 Pause and resume

`run.paused` is a durable suspension in which the backend performs no further run work until a resume condition occurs.

Valid pause causes include an interrupt, unavailable provider, unavailable workspace or dependency, capacity pressure, administrative action, and scheduled continuation. `reason` is an open namespaced string.

`resumeMode` is:

- `automatic`: backend resumes after its recovery condition is met; or
- `command`: an accepted external request is required.

Pause is not an error and not a terminal.

### 6.5 Workflow

Workflow lifecycle types are `workflow.started`, `workflow.finished`, `workflow.error`, and `workflow.cancelled`.

A workflow terminal **MUST NOT** be emitted while any run belonging to it is open. A workflow associated with a client executor may include `defaultClientExecutorId`, but each run **MUST** record its effective binding explicitly in `run.started`.

---

## 7. Core authority events

### 7.1 `run.started`

Required run-tree fields:

- optional `clientExecutorId`;
- optional `workflowId` and required `workflowSequence` when workflow-owned.

The event enters `running`. It occurs exactly once per `runId`.

### 7.2 `run.paused`

Required:

- `waitId`;
- `reason`;
- `resumeMode` (`automatic` or `command`).

Optional:

- `interruptId`;
- `retryAt`;
- user-safe `message`.

At most one pause cycle is open per run.

### 7.3 `run.resumed`

Required:

- `waitId`, matching the active pause.

It closes that pause cycle and returns the run to `running`.

### 7.4 `run.stop.requested`

Required:

- `targetRunId`.

The envelope `runId` identifies the journal owner. `targetRunId` identifies the run being stopped. For a direct stop they are equal.

### 7.5 Run terminals

`run.finished` has no required result payload. It may include `artifactIds` and optional `usage`.

`run.error` requires:

- `code`: opaque machine string;
- `message`: sanitized user-safe text.

It may include partial `usage`.

`run.cancelled` may include `code`, `message`, and partial `usage`.

`usage` is an optional object with token counters and other implementation-defined additive fields. The same shape is reused across all run terminals; partial values are permitted on error and cancellation.

All terminals obey §6.2 and occur exactly once.

### 7.6 `interrupt.requested`

Required:

- `interruptId`;
- `waitId`;
- `kind`: open namespaced string;
- `actions`: non-empty array of allowed decision strings.

Optional:

- `scope`: `run` (default) or `workflow`;
- `expiresAt`;
- `title`;
- `message`;
- kind-specific structured `input`.

When `scope` is omitted or `run`, the event is run-tree authority: `runId`, `rootRunId`, and `sequence` are required, and a blocking interrupt **MUST** be followed consecutively in that run journal by `run.paused` with the same `waitId` and `interruptId`.

When `scope` is `workflow`, the event is workflow authority: `workflowId` and `workflowSequence` are required, and `runId`, `rootRunId`, and `sequence` **MUST** be absent. A blocking workflow-scope interrupt **MUST** be followed consecutively in `workflowSequence` by `workflow.paused` carrying the same `waitId` and `interruptId`. `workflow.paused` **MUST** include `waitId` and `interruptId`.

### 7.7 `interrupt.resolved`

Required:

- `interruptId`;
- `waitId`;
- `decision`.

The decision **MUST** be one of the corresponding request’s `actions`, except backend expiry may use `expired`. Optional `reason` and protected actor provenance may be included.

When the matching `interrupt.requested` `input.members` listed more than one HITL member, `interrupt.resolved` **MAY** include `members`: an array of `{ requestId, toolCallId?, toolName?, toolType?, decision }` with one entry per member. Each member `decision` **MUST** be one of the request `actions` or `expired`. The top-level `decision` remains the pack fold (stop wins; else all approve; else all expired; otherwise deny). There is no protocol `decision=mixed`.

Expiry is represented only as `interrupt.resolved` with `decision=expired`.

Resolution closes the interrupt but does not itself resume or terminate the run or workflow. For `scope=run` the backend subsequently emits `run.resumed` or, after closing all other work, a terminal. For `scope=workflow` the backend subsequently emits `workflow.resumed` or a workflow terminal.

### 7.8 `tool.call.started`

Required:

- `toolCallId`;
- `toolName`;
- `executor` (`backend` or `client`);
- `args`: complete structured arguments, which may be an empty object.

`executor` is immutable. For `executor=client`, the owning run **MUST** have `clientExecutorId`.

This authority event is observational. Clients **MUST NOT** execute a tool because they received it.

### 7.9 `tool.call.finished`

Required:

- `toolCallId`;
- `outcome` (`success`, `error`, or `cancelled`);
- `response`: any JSON value, including `null`.

Optional `code` and user-safe `message` may summarize errors or cancellation.

Exactly one finish follows each start. There are no separate args, result, error, or cancelled event types.

---

## 8. Watch events

Watch events mean “this scope changed; catch up.” They never replace authority history.

Core:

- `session.revision`
- `run.revision`

Profiles add:

- `workflow.revision`
- `node.revision`
- `workspace.revision`
- `session.created`
- `session.updated`
- `session.archived`

Each watch event requires its class fields from §5.1. It may include a typed cursor:

```json
{
  "cursor": {
    "kind": "session",
    "sessionSequence": 42
  }
}
```

Allowed cursor kinds are `session`, `workflow`, and `run`, carrying the corresponding id and sequence.

`session.created`, `session.updated`, and `session.archived` also require the affected `sessionId`.

Ancestor watch events may be coalesced. The emitted revision and cursor **MUST** represent at least the greatest authority advance covered by the notification.

---

## 9. Optional authority profiles

### 9.1 Workflow profile

Workflow events are defined in §6.5. Every event belonging to a workflow, including its run-tree events, carries `workflowSequence`.

### 9.2 Conversation profile

#### User messages

`message.user.recorded` requires:

- `messageId`;
- `messageScope` (`session`, `workflow`, or `run`);
- `content`;
- optional `attachmentIds`.

The envelope and ordering fields must match the selected scope.

#### Streamed messages

`message.text.started` requires:

- `messageId`;
- `role` (`assistant` or `system`);
- `messageScope`.

`message.text.delta` requires `messageId` and `delta`.

`message.text.completed` requires `messageId`.

Events are applied once by canonical event identity and in authority sequence order. A replay **MUST** preserve event IDs; reducers deduplicate before appending deltas.

#### Reasoning

`reasoning.started`, `reasoning.delta`, and `reasoning.completed` use a required `reasoningId`. They may include `messageId` for correlation. Kautuka emits `reasoningId` as `reasoning:{assistantMessageId}` so clients can join reasoning to the parent assistant message. Approval resume may open a later assistant `messageId` (new delivery attempt); transcript UIs **MUST** keep earlier reasoning for the same turn rather than dropping it when the final text lands on the later id. Reasoning content is display-sensitive and may be omitted from a principal’s conversation projection, but its omission **MUST NOT** alter backend execution authority.

#### Transcript visibility

The default primary transcript includes session-scoped messages and messages for the active, or most recently started, top-level run. Nested-run messages are shown only when a client explicitly opens that run.

### 9.3 Transcript snapshots

`messages.snapshot` is a hydrate record, not an authority append. It requires:

- `snapshotId`;
- `sessionId`;
- `coversThroughSessionSequence`;
- `messages`.

The client replaces transcript state covered by the snapshot, then applies authority events with greater `sessionSequence`. A snapshot has `protocolVersion` and `timestamp` but no `eventId` or authority sequence.

### 9.4 Conversation redaction

`message.redacted` requires `messageId`, `reason`, and `displayReplacement` (which may be `null`). It changes conversation display only. It does not erase or alter backend operational facts, tool payloads, or prior journal bytes.

### 9.5 Attachments and artifacts

Attachment types are:

- `attachment.added`
- `attachment.ready`
- `attachment.failed`
- `attachment.removed`

Events carry metadata and authorized references. Raw bytes **MUST NOT** be embedded in Narad events.

Artifact types are:

- `artifact.added`
- `artifact.ready`
- `artifact.failed`

Artifacts are produced outputs; attachments are admitted inputs.

### 9.6 State and UI profiles

`state.snapshot` requires `scope`, `revision`, and `state`.

`state.delta` requires `scope`, `baseRevision`, `revision`, and an RFC 6902 `patch`. `revision` **MUST** equal `baseRevision + 1`.

`state.compacted` requires `scope`, `coversThroughSessionSequence`, and `snapshotRevision`.

`ui.state.snapshot` and `ui.state.delta` follow the same revision rules. `view.opened`, `view.updated`, and `view.closed` require `viewId`; updates must state `operation=replace` or `operation=patch`.

### 9.7 Graph profile

`graph.snapshot` and `graph.delta` are **hydrate records**, not authority events. They are deterministic derived projections under the negotiated `narad.graph/v1` profile. They **MUST NOT** carry or consume `sessionSequence`, `workflowSequence`, or run-tree `sequence`. They **MUST NOT** override run, tool, or interrupt authority, and graph existence **MUST NOT** imply a workflow.

Both record types require `sessionId`, `scope`, `graphSchemaVersion`, graph `revision`, and a source cursor anchored to committed authority:

- `run`: requires an already-started `rootRunId` in that session and `sourceRunCursor` with required `sequence` and optional `rootRunId`. The cursor **MUST** reference a positive committed run-tree sequence no greater than the latest sequence observed in that root journal at emit time. `workflowId` **MUST NOT** be present.
- `workflow`: reserved for future orchestration support; requires `workflowId` and `sourceWorkflowCursor` with required `sequence` and optional `workflowId`.

Snapshots additionally require `rootNodeId`, `nodes`, and `edges`. Deltas require `baseRevision`, `revision`, and RFC 6902 `patch`. Graph revisions must be contiguous within a scope key. Across increasing graph revisions within the same scope key, the source cursor sequence **MUST NOT** regress.

**Deterministic reproducibility.** Given the same ordered source authority and `graphSchemaVersion`, implementations **MUST** derive byte-equivalent canonical graph state, deterministic node and edge identities, deterministic revision progression, and deterministic delta order. Rebuilding from the authority journal **MUST** reproduce any emitted snapshot exactly. Graph state **MUST NOT** affect lifecycle.

**Delivery.** On attach or revision gap, servers **SHOULD** send `graph.snapshot`. Clients **MAY** apply live `graph.delta` only after they hold authority through the record’s source cursor. On delta gap, clients **MUST** stop applying deltas and request a snapshot.

The wire envelope uses `protocolVersion`; graph payload schemas use distinct names such as `graphSchemaVersion`.

### 9.8 Progress profile

`progress.recorded` is an append-only, run-scoped activity fact with no lifecycle authority. It requires:

- run-tree ordering fields (`rootRunId`, `runId`, `sequence`);
- `progressId`: stable identity for the activity item;
- `category`: open namespaced string;
- `label`: user-visible summary;
- optional `detail`;
- `status` (`active`, `completed`, `failed`, or `cancelled`);
- `privacy` (`public`, `internal`, or `sensitive`).

The referenced `runId` **MUST** belong to an already-started run in the same `sessionId` and `rootRunId` lineage. Progress **MAY** be emitted while the run is `running` or `paused`. Progress **MUST NOT** be emitted after that run reaches a terminal. Progress **MUST NOT** interleave between `interrupt.requested` and the matching consecutive `run.paused` or `workflow.paused`.

Progress events are replayed by canonical event identity and run-tree sequence. They **MUST NOT** open, close, pause, resume, or terminate runs.

### 9.9 Extensions

Extensions use only:

- `session.custom` / `session.raw`
- `workflow.custom` / `workflow.raw`
- `run.custom` / `run.raw`
- `node.custom` / `node.raw`

`*.custom` requires a namespaced `name` and JSON `value`. `*.raw` requires `contentType` and an opaque encoded `payload`.

Extensions **MUST NOT** redefine core lifecycle meaning or close runs. Unknown extension names are ignored by core reducers.

---

## 10. Replay, history, and delivery

### 10.1 Delivery

Delivery is at least once. Reducers deduplicate events before applying them.

For SSE, frame `id` **MUST** equal `eventId` for authority and watch events. Control messages and snapshots do not use authority event IDs.

One frame carries one authority event, watch event, hydrate record, or control message. Transport disconnection does not imply a domain outcome.

### 10.2 History

A conforming server **MUST** expose session authority history with a cursor equivalent to:

```http
GET /sessions/{sessionId}/events?afterSessionSequence={n}&limit={n}
```

The response is ordered by `sessionSequence` and includes a `nextSessionSequence` cursor when more events remain.

Optional filtered history may use:

```http
GET /sessions/{sessionId}/events
  ?rootRunId={id}&afterSequence={n}
  &workflowId={id}&afterWorkflowSequence={n}
```

When multiple cursor kinds are supplied they **MUST** identify a mutually consistent position; otherwise the server rejects the request as a cursor conflict.

### 10.3 Subscribe before replay

Live attach and replay must avoid a gap. A transport **MUST** either:

- atomically subscribe and then replay from the accepted cursor; or
- return a boundary cursor, subscribe beyond it, and backfill through that boundary.

Live and replayed events merge idempotently by canonical event identity.

### 10.4 Capability negotiation

Before authority delivery, the server provides:

- `protocolVersion`;
- `activeProfiles`;
- history and heartbeat limits;
- current `sessionSequence`;
- active top-level execution identity, if any.

The wire endpoint is transport-specific. It must be available before profile events are consumed.

---

## 11. Client execution and heartbeat control

Control messages are not authority events. They have no `eventId`, authority sequence, or revision.

### 11.1 Client attachment and lease

An execution-capable client attaches to a logical `clientExecutorId`. The backend returns:

```json
{
  "type": "client.attached",
  "protocolVersion": "narad/v1",
  "clientExecutorId": "desktop-worker-123",
  "leaseId": "opaque-fencing-token",
  "heartbeatIntervalMs": 15000,
  "leaseExpiresAt": "2026-08-05T04:00:00Z"
}
```

The client may negotiate the generic `client-executor` capability. It does not register or advertise tool names. Backend configuration determines which tools may be assigned to that executor.

Reattachment creates a new `leaseId`. An expired or superseded lease **MUST NOT** accept new executions. A control already accepted under the prior lease may finish with its original execution token until that token expires; this is the only stale-lease exception.

### 11.2 Heartbeats

There are exactly two heartbeat directions:

- `server.heartbeat`: backend to client;
- `client.heartbeat`: client to backend.

They report peer/connection liveness, not session, workflow, or run state.

`client.heartbeat` includes `clientExecutorId` and `leaseId`. A valid heartbeat renews that lease. Heartbeat intervals and expiry are negotiated during attachment.

Missing heartbeats mark the endpoint unavailable. They **MUST NOT** by themselves terminate a run. If a run requires the unavailable executor, the backend may append `run.paused` with `reason=client_executor_unavailable` and resume after a valid lease returns.

There are no session, workflow, or run heartbeat events.

### 11.3 Targeted tool execution

After appending `tool.call.started` with `executor=client`, the backend sends exactly one targeted control message to the bound executor:

```json
{
  "type": "client.tool.execute",
  "protocolVersion": "narad/v1",
  "clientExecutorId": "desktop-worker-123",
  "leaseId": "opaque-fencing-token",
  "executionToken": "single-call-capability",
  "executionTokenExpiresAt": "2026-08-05T04:01:00Z",
  "toolCallId": "tc-1",
  "toolName": "open.file",
  "args": {}
}
```

Rules:

1. The transport **MUST NOT** deliver this control message to another client executor.
2. A client executes tools only from `client.tool.execute`, never from `tool.call.started` or replay.
3. `toolName` and `args` **MUST** exactly equal the corresponding `tool.call.started` payload.
4. `executionToken` is bound to the authenticated executor, originating lease, tool call, and expiry.
5. Before performing any side effect, the client **MUST** durably record the token as accepted. Duplicate delivery of an accepted token **MUST NOT** execute the tool again.
6. The client returns `client.tool.finish` with the token, `outcome`, and `response`.
7. The backend validates the token before appending `tool.call.finished`. The terminal event’s `outcome` and `response` **MUST** equal the accepted finish control.
8. A token represents one logical finish. Repeating an identical finish is idempotent and **MUST NOT** append another terminal; reusing it with different data is rejected. A finish for an accepted, unexpired token may use its originating lease after that lease has been superseded; the new lease cannot claim that token.
9. Every execution token has `executionTokenExpiresAt`. If no valid finish arrives by expiry, the backend closes the call with `outcome=error` and required `code=client_execution_expired`, unless stop policy closes it as cancelled.
10. The same `toolCallId` **MUST NOT** be reassigned after execution is accepted. A retry that could repeat side effects uses a new `toolCallId`.
11. If delivery is not accepted, the backend appends `tool.call.finished` with `outcome=error` and required `code=client_unavailable` or `delivery_failed`. A stop may instead produce `outcome=cancelled`.

Observable authority and executable delivery are therefore separate.

---

## 12. Security and privacy

- Backends are the sole authority-event issuers.
- Authorization is enforced by the backend and transport.
- Client execution controls require authenticated, targeted, fenced delivery.
- Public errors must be sanitized.
- Tool arguments and responses are protected operational data and are delivered only to authorized authority consumers.
- Conversation redaction affects display state only.
- Trace/debug profiles must be authorization-gated and cannot affect lifecycle.
- Raw extension payloads are untrusted and must not trigger execution.
- Clients must not infer authority from local timeouts, network errors, or missing heartbeats.

---

## 13. Conformance

A core server conforms when it:

1. emits only valid core authority events and negotiated profile events;
2. satisfies all applicable ordering domains;
3. preserves event identity on replay;
4. enforces run, interrupt, tool, and terminal invariants;
5. provides gap-free history and live attachment;
6. separates targeted controls from authority events; and
7. passes the published core schemas and golden traces.

A client conforms to a profile when it:

1. validates the profile’s required envelopes;
2. reduces authority deterministically in sequence order;
3. deduplicates before applying events;
4. never executes authority events;
5. executes only authenticated targeted client-tool controls; and
6. does not invent terminals.

Unknown protocol versions must be rejected. Unknown optional fields are ignored. Unknown event types from an active claimed profile fail closed unless that profile explicitly marks them ignorable.

The JSON schemas define structural validity. The golden traces define lifecycle and replay behavior. Where prose, schema, and trace disagree during the release-candidate period, the conflict is a specification defect and must be resolved before the v1 freeze.
