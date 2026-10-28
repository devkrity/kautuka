# Troubleshooting

Use **symptom → check → fix → verify**. Start with Dashboard health and preserve the exact error, status code, agent ID, and session ID.

## First run does not appear

Check `GET /api/auth/setup/status`. If `needsSetup` is true, open `/first-run`; if false, sign in normally. Creating the first admin redirects to onboarding. A cleaned runtime with an existing user database will not repeat first-user setup.

## Ollama cannot connect

Use **Settings → Providers → Test**. Confirm Ollama is running, the model appears in `/api/tags`, and the base URL is reachable from Gateway’s network context. `localhost` in the browser is irrelevant if Gateway runs in WSL, a VM, or another host context. Ollama needs no Kautuka API key.

## No agents or chat cannot start

Create provider → model → agent, then select that agent. Check `GET /api/narad/v1/capabilities` and session watch if a saved agent is missing from the workspace.

## Connector tools are missing

Test the connector, save required credentials, refresh discovery, enable it, then attach it to the agent. Check the agent’s effective tool policy. Local commands must map to a supported managed-container runtime.

## Run waits after approval

Confirm the request moved from Pending to History, then reopen the related session. Check whether the action was approved, denied, expired, or stopped and whether it belongs to an outer agent-tool run. Do not submit a second decision to force continuation.

## Channel does not receive or route messages

Run the channel HITL test, check ChannelHost health, token/app permissions, destination ID, default target agent, and tether setting. Test outbound and inbound separately.

## Auto-Pilot did not run

Check mode, pause state, local-time cron, pre-run approval, overlap policy, and run history. Use Run now to separate scheduling from agent execution.

## Memory is absent

Enable fact extraction on the agent, complete the turn successfully, and use an explicit lasting statement. Extraction is selective. Check the correct agent on `/memories`.

## 401 or 403

401 means the local session is missing or expired; sign in again. 403 means the authenticated user lacks ownership or administrator access; verify role and agent permissions instead of retrying the same request.
