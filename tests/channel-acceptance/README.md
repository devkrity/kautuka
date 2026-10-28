# Cross-surface acceptance workspace

This workspace implements the CRH-030 through CRH-054 Playwright foundation (plus CRH-060–066 docs/contracts). It uses typed TypeScript scenario records; it does not introduce a JSON/YAML DSL and does not alter Kautuka authentication or production behavior.

The test suite owns processes it starts, records their PIDs, waits for explicit readiness URLs, and removes its per-run workspace. Reusing a running stack is permitted only when an approved execution constructor explicitly sets `reuseExistingStack: true`.

The concrete Development/Test executor uses the approved standalone simulator: set `KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI` to its explicit HTTP loopback URI and `KAUTUKA_ACCEPTANCE_WEB_BASE_URI` to the stack URL. It generates a per-run simulator control key, starts both processes, and supplies the approved single simulator URI to ChannelHost. An alternate approved `KAUTUKA_ACCEPTANCE_EXECUTOR_MODULE` may export `createAcceptanceExecution`.

Install the workspace dependencies, install Playwright browsers, then run `npm test` from this directory. Without an executor module, only the local contract/evidence tests execute; cross-surface cases are visibly skipped and must not be reported as a full pass.

Install this directory as its own workspace (`pnpm install --ignore-workspace` from here, or rely on the nested `pnpm-workspace.yaml`). The repo-root workspace is only Narad tooling; walking up to it leaves Playwright uninstalled. Hosted `test:contract` also runs `pnpm install --frozen-lockfile` at the repo root because `narad-conformance.ts` resolves `@devkrity/narad-spec` from there; that package is not an acceptance dependency.

`pnpm --dir tests/channel-acceptance test:contract` is the hosted-CI subset. Live CRH-030/033/031 (browser smoke, HITL approve, Telegram setup) need Ollama and a tool-capable model on a self-hosted runner. Record pass/fail per scenario ID outside the repo before tagging; do not treat a contract-only green as a live HITL pass.

`tests/installed-stack-live.spec.ts` is the installed-appliance browser subset (login/navigation/chat, HITL approve, Telegram settings after Edit). Point it at a live WebHost with `KAUTUKA_ACCEPTANCE_WEB_BASE_URI`, `KAUTUKA_ACCEPTANCE_USERNAME`, `KAUTUKA_ACCEPTANCE_PASSWORD`, and `KAUTUKA_ACCEPTANCE_AGENT_ID`. It is not in `test:contract` and is not a CRH-031 bind with a real bot token.

## Acceptance-code auditability

Scenario records own the scenario ID, checklist coverage, tags, and top-level assertion intent. Narrow helpers own one boundary: browser actions, provider fixtures/events, public bootstrap, evidence, process ownership, or resilience measurement. Do not hide assertions in generic retry helpers.

Use user-facing roles, labels, placeholders, or deliberate `data-*` contracts for browser interaction. A raw structural/class selector needs a UI contract change first; it is not an acceptance shortcut. Provider-shaped payloads and transcript assertions belong in `provider-events.ts`; provider credentials/destinations belong in `provider-fixtures.ts`.

The contract test enforces no runtime dependencies, an allowlisted development dependency set, formatting hygiene, no raw structural/class selectors, no repository-source reach-through, bounded file size, no raw sleeps, one provider transcript assertion owner, and required declarative scenario fields. Update the test with a focused reason when this workspace deliberately gains a new dependency or boundary; do not weaken it to accommodate incidental growth.
