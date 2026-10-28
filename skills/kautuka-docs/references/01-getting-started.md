# Getting started

Before chat can run, configure a provider, a model served by that provider, and an agent that uses the model.

## First run

1. Open `/first-run` and create the first administrator account. The password must be at least eight characters.
2. Continue to `/onboarding`, or choose manual configuration.
3. Create or select an AI provider, save a model, then create an agent that uses that model.
4. Open `/agent`, select the agent, start a new session, and send a simple prompt.
5. Add connectors, skills, channels, approvals, and Auto-Pilot only as needed.

The onboarding presets currently cover Gemini, Ollama, and OpenRouter, but manual setup exposes every provider type in the platform catalog. The wizard can also recommend default connectors and skills and optionally configure Telegram.

## Navigation

- Dashboard: appliance health, recent sessions, and update status.
- Agents: chat and session history.
- Approvals: pending and resolved human decisions.
- Auto-Pilot: schedules, runs, and notifications.
- Connectors and Skills: reusable agent capabilities.
- Memories: facts and sessions by agent.
- Usage: run and token usage.
- Settings: administrator-only providers, models, agents, users, organization, and advanced settings.

## Endpoints and verification

- Setup/login: `/api/auth/setup/status`, `/api/auth/setup`, `/api/auth/login`, `/api/auth/me`.
- Onboarding health is derived from `/api/runtime/system-health` plus the settings catalogs.
- Verify success by opening the created agent and completing one streamed response. A provider/model alone is not runnable until an agent references the model.
