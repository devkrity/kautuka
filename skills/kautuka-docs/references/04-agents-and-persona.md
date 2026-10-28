# Agents and persona

Agents are administrator-created runtime configurations. Configure them in **Settings → Agents** and use them from `/agent/{agentId}`.

## Main settings

- Name, model, and system prompt define the basic assistant behavior.
- Persona adds tone, style, verbosity, role, and organization context; it does not override permissions or approvals.
- Sandbox enables workspace-backed tool work.
- Retained sessions controls how many older sessions remain after a new session starts. The active session context is retained.
- Summary model can differ from the run model.
- Memory fact extraction can persist durable facts after completed turns.

## Connectors, skills, and agents as tools

The Connectors tab selects MCP connectors and per-tool modes: off, on, or ask. The Skills tab attaches skills as disabled, trusted, or ask first.

An agent can be exposed for another agent to invoke as a tool. Give it a clear tool description and invocation timeout, then explicitly allow it from the caller agent. Agent-tool composition is leaf-oriented: an agent exposed as a tool cannot itself be configured to invoke other agents as tools. Approval can be required for selected agent-tool targets.

## Endpoints and verification

- List: `GET /api/settings/agents` (includes `revision`, the current definition snapshot number)
- Save/delete: `PUT /api/settings/agents/{agentId}`, `DELETE /api/settings/agents/{agentId}`
- Effective tool policy: `GET /api/settings/agents/{agentId}/tool-policy`
- Configuration history: `GET /api/settings/agents/{agentId}/revisions`
- Consecutive (or `?from=`) config diff: `GET /api/settings/agents/{agentId}/revisions/{revision}/diff`
- Persona definitions/profiles: `/api/settings/persona/definitions`, `/api/settings/persona/profiles`
- Runtime/session transport: `/api/narad/v1`

Verify with a new session. For agent-as-tool, invoke it from the permitted caller and confirm one outer tool call contains the child activity and any required approval.
