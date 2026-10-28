# Runtime and observability

Dashboard is the operational starting point. It combines system health, appliance health, recent sessions, update state, and update history. Usage shows per-run and summarized consumption.

## Advanced settings

Administrator-only **Settings → Advanced** contains:

- Workspace: effective workspace root, default sandbox image, CPU, memory, and timeout. Package cache stats load when an administrator opens Settings → Advanced → Workspace (`GET /api/runtime/diagnostics/pkgs-proxy/stats`) with a refresh control; they are not stored and are not shown to non-admins.
- Document Ingestion: enablement, parser connector/tool, timeout, extensions, PDF image fallback, and vision routing.
- Updates: stable or beta release track. Track changes never downgrade; switching to stable waits for a newer stable release.
- Runtime: structured-output adaptation controls.
- Observability: system log level and optional raw-message capture. Raw content increases sensitive-data exposure in logs and traces.
- Persona, Provider Catalog, Provider Path, and Memory defaults.

Agent files are managed from the agent chat surface and under `/api/agents/{agentId}/files`. Session assets use `/api/sessions/{sessionId}/assets`. Sandboxes are Manager-owned, read-only-root containers with configured resource limits; per-agent sandbox enablement controls whether workspace tool work is available.

## Endpoints and verification

- Health: `/api/runtime/system-health`, `/api/runtime/appliance-health`
- Updates/history: `/api/runtime/update-status`, `/api/runtime/update-history`
- Manager diagnostics: `/api/runtime/diagnostics/manager/containers`, `/api/runtime/diagnostics/pkgs-proxy/stats`
- Usage: `/api/runtime/usage/runs`, `/api/runtime/usage/summary`
- Settings: `/api/settings/system`, `/api/settings/advanced`, `/api/settings/provider-path-policy`, `/api/settings/memory`

After a change, run a representative agent task and recheck Dashboard. For update apply/revert, expect service restart handoff and verify the reported current version after the stack returns.
