# Protocol reference

## Public HTTP

Gateway endpoint composition lives in `src/backend/Kautuka.Gateway/Endpoints`. The major surfaces are:

- `/api/auth`: setup, login, refresh, logout, current user;
- `/api/narad/v1`: Narad discovery, session watch, attach, run, thread commands, and workspace inspect;
- `/api/approvals`: pending/history/detail (inbox only; not aggregates). Resolve is `POST /api/narad/v1/commands/resolve-interrupt`;
- `/api/agents/{agentId}/stats` and `/stats/denied`: on-read human approve/deny aggregates for that agent (nested child HITL via `outer_agent_id`), optional `days=7|30|all` window, optional denied filters `toolName` / `agentDefinitionRevision` / `ungrouped=1`;
- `/api/connectors/{serverId}/stats` and `/api/skills/{skillId}/stats` (plus `/denied`): catalog-scoped aggregates across agents;
- `/api/settings`: agents (including per-agent revision history and consecutive setting diffs), providers, models, users, organization, system, memory, policies;
- `/api/connectors`, `/api/skills`, `/api/catalog`;
- `/api/sessions` (including conversation asset upload, metadata list, and byte download), `/api/memories`, `/api/autopilot`, `/api/runtime` (including admin diagnostics `GET /api/runtime/diagnostics/manager/containers` and `GET /api/runtime/diagnostics/pkgs-proxy/stats`);
- internal management endpoints. Channel endpoint definitions exist in source but are not mapped by the current channel module and are not a callable API surface.

OpenAPI is mapped by Gateway; Scalar is Development-only. Treat executable endpoint composition—not the presence of a mapping class—as the route authority.

## Internal gRPC

| Service | Contract |
|---|---|
| Manager | `src/shared/Kautuka.Manager.Protos/Protos/manager.proto` |
| CredentialsHost | `src/shared/Kautuka.CredentialsHost.Protos/Protos/credentialshost.proto` |
| ChannelHost | `src/shared/Kautuka.ChannelHost.Protos/Protos/channelhost.proto` |
| UpdateService | `src/shared/Kautuka.UpdateService.Protos/Protos/updateservice.proto` |

Generated types are build output; do not hand-maintain duplicate DTOs. New mutating operations require explicit deadlines, cancellation, bounds, typed outcomes, authorization, and idempotency where retries are possible.

## Agent Interaction Protocol (Narad)

Release candidate: [Agent Interaction Protocol](agent-interaction-protocol.md). Narad (`narad/v1`) is the **sole production wire protocol** between Gateway and the browser. It defines session-ordered authority events with optional workflow and presentation profiles. Durable authority events are separate from targeted client-tool controls and bidirectional heartbeat traffic. See the non-normative [design rationale](agent-interaction-protocol-rationale.md) and [Kautuka adoption guide](agent-interaction-protocol-kautuka-adoption.md).

Browser chat attaches to `/api/narad/v1` session watch streams, reduces authority events with `@devkrity/narad`, and renders with `@devkrity/narad-react` plus Kautuka selectors in the web app. There is no AG-UI, CopilotKit, or assistant-ui runtime in the production SPA.

## Historical note

Prior releases projected canonical runtime events toward AG-UI-shaped SSE at `/api/agent-runtime` and shipped `@kautuka/ag-ui-*` client packages with CopilotKit/assistant-ui conformance examples. That surface has been removed. Rationale for the earlier interoperability boundary remains in [ADR 0006](../adr/0006-ag-ui-interoperability-and-product-ux.md) (superseded). Narad adoption mapping is in [Kautuka adoption of Narad](agent-interaction-protocol-kautuka-adoption.md).
