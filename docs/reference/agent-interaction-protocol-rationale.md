# Narad design rationale

**Status:** Non-normative companion to [Narad](agent-interaction-protocol.md)

This document records why Narad uses its current model. It does not add conformance requirements.

## One authority vocabulary

The backend journal and product stream use the same domain event meanings. Compatibility adapters may translate at an external boundary, but Narad does not require another protocol’s lifecycle.

## Sessions, workflows, and runs

- A session is the durable conversation and global replay boundary.
- A workflow is optional orchestration.
- A run is execution.
- Nested runs share a root run journal.

`sessionSequence`, `workflowSequence`, and run-tree `sequence` intentionally overlap. Each answers a different replay question without relying on timestamps:

- What happened in this session?
- What happened in this workflow?
- What happened in this run tree?

## Backend authority and request APIs

Requests are transport operations, not durable facts. Accepted effects become domain events. Keeping generic `command.*` chrome out of core avoids duplicating every domain lifecycle with a second operation lifecycle.

## Pause and interrupt

Pause and interrupt are distinct:

- `run.paused` says execution is durably suspended.
- `interrupt.requested` says an external decision or input is required.

Interrupts are one pause cause. Other recoverable causes include provider, workspace, dependency, capacity, administrative, and scheduled waits. Pause remains non-terminal so recovery can continue the same run.

## Tools

Tool lifecycle is deliberately two events:

- `tool.call.started`: complete arguments and executor;
- `tool.call.finished`: outcome and response.

There are no separate argument, result, error, or cancellation event families.

Tool catalogs are backend/model configuration and are not required by the UI protocol. The UI needs only the calls it is authorized to observe and, for its bound executor, targeted execution controls.

## Client execution

A run binds one logical client executor at start. This avoids selecting “any connected tab” at tool-call time and makes scheduled execution deterministic.

Authority events are never executable instructions. A separate targeted control carries a fenced, single-use capability to the bound client. This prevents replay and observer streams from becoming accidental side-effect triggers.

## Heartbeats

Only network peers heartbeat:

- server to client;
- client to server.

Sessions, workflows, and runs are durable state machines, not network peers. Their state comes from authority events. Missing heartbeat means connection or executor unavailability, never an inferred run terminal.

## Profiles

Core contains execution authority only. Conversation, workflow, state, UI, graph, attachments, artifacts, workspace watch, trace, debug, and extensions are explicit profiles. Clients are obligated only for profiles they negotiate.

## Redaction

Conversation redaction is a display concern. It does not rewrite backend operational facts or silently remove required tool payloads from an authorized authority stream.

## Implementation freedom

Narad does not prescribe:

- databases or transaction mechanisms;
- scheduler algorithms;
- model/provider selection;
- timeout values;
- executor-selection policy;
- HTTP route shapes;
- UI components; or
- internal worker health checks.

It specifies the externally observable facts that all conforming implementations must reduce the same way.
