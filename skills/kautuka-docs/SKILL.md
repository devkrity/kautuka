---
name: kautuka-docs
description: Operational help for the Kautuka appliance: setup, providers, models, agents, MCP connectors, skills, channels, approvals, Auto-Pilot, memory, users, runtime health, and troubleshooting.
metadata:
  author: kautuka-platform
  package: kautuka-docs
  packageVersion: 1.0.0
---

# Kautuka Help Router

Use this skill for questions about operating the Kautuka product. Kautuka supplies the orchestration platform, default MCP packages, and default skills. Administrators configure the model providers, models, and agents used for runs.

## Answer format

1. Give the direct answer in plain language.
2. State the effect and any safety or durability consequence.
3. Name the exact page and relevant `/api` endpoint family.
4. Give one short verification step.

Do not guess. If the references do not cover the question, say what is unknown and direct the user to Dashboard health, the relevant Test or Check action, or the exact failing request.

## Route by topic

- First run, onboarding, login, and navigation: [Getting started](references/01-getting-started.md)
- AI service types, endpoints, and credentials: [Providers](references/02-providers.md)
- Model discovery, save, and capability checks: [Models](references/03-models.md)
- Agent configuration, persona, sessions, sandbox, and agent-as-tool: [Agents](references/04-agents-and-persona.md)
- MCP connector setup, credentials, testing, and discovery: [Connectors](references/05-connectors.md)
- Skill import, validation, enablement, and agent attachment: [Skills](references/06-skills.md)
- Telegram and Slack setup: [Channels](references/07-channels.md)
- Human approval requests and run continuation: [Approvals](references/08-approvals.md)
- Scheduled agent runs: [Auto-Pilot](references/09-autopilot.md)
- Durable facts and session history: [Memory](references/10-memory.md)
- Accounts, roles, access, profiles, and organization defaults: [Users and organization](references/11-users-auth-organization.md)
- Appliance health, updates, workspace, sandbox, files, usage, and observability: [Runtime and observability](references/12-runtime-and-observability.md)
- Symptom-led recovery: [Troubleshooting](references/13-troubleshooting.md)

Use “session” for the user-visible conversation. Use “thread” only when a protocol or API name still uses that word.
