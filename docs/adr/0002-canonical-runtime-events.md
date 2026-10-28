# ADR 0002: Canonical runtime events own run-visible state

**Status:** Accepted

## Decision

Per-agent `runtime_events` and root sequence are the canonical run-visible history. Narad, browser state, in-memory hubs, and transcript projections derive from that history.

## Evidence

The per-agent schema commits runtime events and outbox records; `RuntimeEventPublisher`, `RuntimeEventHistoryReader`, `CanonicalRuntimeThreadProjector`, and `AgentRuntimeEventStreamWriter` persist, validate, project, and deliver the root stream.

## Consequences

- Reconnect replays canonical history rather than merging client caches.
- Nested child events retain exact root/parent identities.
- HITL and AAAT continuation reuse the original root lineage.
- Invalid history fails projection instead of receiving compatibility repair.
- A separate canonical `runs.db` or browser-owned transcript is not supported.
