# Approvals (HITL)

Human approval pauses a guarded action until an authorized user decides it or the request expires. Requests may come from MCP tools, skill use, agent-as-tool invocation, or an Auto-Pilot pre-run gate.

## Configure

- Agent connector tools use off, on, or ask modes.
- Selected agent-as-tool targets can require approval.
- Skills can be attached as Ask first.
- Auto-Pilot mode can require approval before each scheduled run.

## Resolve

// Approve all for this run is not a product HITL action; configure tools/skills instead.

Open `/approvals`. Pending cards show the tool, arguments, agent, outer agent/session when nested, requester time, and expiry. Available actions are always: **approve**, **deny**, or **stop run**. History records the decision, actor, source channel, reason, and time when available.

Resolving from the Approvals page or a channel updates the canonical request. The waiting run resumes after **approve**; **deny** returns a denied tool outcome so the LLM continues; **stop run** terminates that run (and if the run was a child agent, the parent receives a cancelled/stopped tool result). Repeated resolutions are not a second decision.

## Endpoints and verification

- Pending/history: `GET /api/approvals/pending`, `GET /api/approvals/history`
- Session/detail: `/api/approvals/session/{sessionId}`, `/api/approvals/{requestId}`
- Resolve: `POST /api/narad/v1/commands/resolve-interrupt` with `{ interruptId, decision, reason? }` (optional `stop_run` appends canonical `run.stop.requested` through the durable stop command service)
- Stop active root: `POST /api/narad/v1/agents/{agentId}/sessions/{sessionId}/commands/stop-active-root`

Verify that the request leaves Pending, appears in History with attribution, and the related session reaches the expected continued or terminal state.
