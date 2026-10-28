# Narad authority event catalogue

**Catalogue version:** 1
**Protocol:** [`narad/v1`](agent-interaction-protocol.md)

This is the Kautuka audit index for authority events persisted by Gateway and delivered through session streams and history. Narad Protocol coordinates how the agent host shares that state with clients; replay and catch-up are recovery capabilities supported by Kautuka event storage, not the protocol definition. Normative semantics, schemas, and conformance traces live in the [Agent Interaction Protocol](agent-interaction-protocol.md). Product wiring and migration notes are in the [Kautuka adoption guide](agent-interaction-protocol-kautuka-adoption.md).

## Record classes

| Class | Meaning | Examples |
|---|---|---|
| Authority | Immutable journal facts with `eventId` and `sessionSequence` | `run.started`, `tool.call.started`, `progress.recorded` |
| Watch | Revision notifications; not a substitute for authority replay | `session.revision`, `workspace.revision` |
| Hydrate | Derived projections anchored to authority cursors | `messages.snapshot`, `graph.snapshot`, `graph.delta` |
| Control | Transport attachment/heartbeat/client-tool execution | `client.attached`, `server.heartbeat` |

## Core authority events

| Event | Public stream | Notes |
|---|---|---|
| `run.started` | yes | Opens a run scope |
| `run.paused` / `run.resumed` | yes | Interrupt lifecycle |
| `run.stop.requested` | yes | Actor identity redacted on public delivery |
| `run.finished` / `run.error` / `run.cancelled` | yes | Terminal outcomes. `run.finished` may include optional `response` (final assistant text) for channel/hitl projectors when the produce loop stamps it; not a substitute for `message.text.*`. ChannelHost HITL mode sends Telegram `agent_response` from `run.finished` only; mirror still uses one-shot `assistant.message.completed`. |
| `interrupt.requested` / `interrupt.resolved` | yes | HITL boundaries. Browser outer resolve enqueues durable continuation via `ResumeWaitingRoot`; nested child HITL enqueues on the child session (`OuterSessionId`). One packed model turn uses one interrupt id (pack head); sibling cards are approval-store rows, not extra Narad interrupts. |
| `tool.call.started` / `tool.call.finished` | yes | Negotiated conversation profile. Gate-before-start HITL: after outer approve, parent authority must emit `tool.call.started` for the approved `toolCallId` before nested AAAT work (clients must not invent tool rows). Packed independent tools journal starts only after `run.resumed`. |
| `session.revision` | no | Operator/watch surface only |

## Profile authority events

| Profile | Event | Public stream | Notes |
|---|---|---|---|
| Conversation | `message.*`, `reasoning.*` | yes | Transcript and reasoning family. `message.user.recorded` stamps `sourceChannel` (admit surface: `chat`, `telegram`, …) and `agentId` for channel projection / AAAT isolation. Channel projectors skip channel-origin user re-echo and require surface `agentId` match. |
| Progress | `progress.recorded` | public only | `privacy=public`; internal/sensitive omitted. Public `agent.turn` facts may carry `usage`. HITL continuation under the same Narad `runId` must allocate a new `progressId` (`agent-turn:{runId}:{monotonicOrdinal}`) so live reducers do not last-win the first invoke. Chat and trace headers sum completed `agent.turn` usage. `run.finished.usage` is that invoke only. |
| Graph | hydrate only | yes | `graph.snapshot` / `graph.delta` are hydrate records. Status patches replace the full `/nodes` array and clone `JsonNode`s on serialize so fold/attach never re-parents nodes (stream 500). Clients must actually apply patches (`mutateDocument=true`). Chat UI hides nested/AAAT “workflow” topology until a dedicated workflow surface exists. |
| Trace | `trace.event` | no | Operator diagnostics |
| Workspace watch | `workspace.revision`, `session.created/updated/archived` | watch endpoints | Not session authority replay |

## Public delivery rules

Public session streams and paginated history apply `NaradPublicAuthorityFilter`:

- Deliver negotiated client-visible authority: public progress, tool calls, reasoning, conversation, and core run/interrupt events.
- Suppress `trace.event` and `session.revision`.
- Omit `progress.recorded` unless `privacy` is `public`.
- Redact protected identity fields (`actorId`, `userId`, `ownerUserId`, `createdById`) from delivered copies without mutating stored journal JSON.
- Keep `agentId` on public delivery so clients and channel projectors can bind surface vs nested work.

Executable coverage: `NaradSecurityTests`, Narad conformance traces in `@devkrity/narad-spec`, and channel acceptance scenarios `Narad-004`, `Narad-006`, and `Narad-007`.
