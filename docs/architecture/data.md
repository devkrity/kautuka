# Data model

Kautuka uses separate stores with explicit authority. In-memory objects are reconstructible projections or bounded delivery mechanisms.

## Store ownership

| Store | Authority |
|---|---|
| `kautuka.db` | users/auth sessions, providers/models, agent registry, MCPs/skills, settings and immutable configuration revisions, approvals, waits, AAAT invocation/link records, continuation jobs, channels, autopilot, asset metadata/upload staging, quarantine, and usage staging |
| `<shared-state-root>/agents/<locator>/agent.db` | agent definition versions and revision history (`agent_definition_versions`, `agent_definition_settings`, `agent_definition_current`), sessions/messages, runs, canonical runtime events and outbox, memory, embeddings, continuation checkpoints, resolved tool-surface catalogue (`agent_tool_surface_current`) |
| `kautuka-usage.db` | idempotent analytical usage facts; one row per model turn, keyed by a unique fact `run_id` (not the Narad run id) |
| `gateway-auth.key` | Gateway HMAC access-token signing identity; loss invalidates existing access tokens and changes signing identity |
| CredentialsHost SQLite store | encrypted user/system credentials, mutation receipts, audit, and key-rotation checkpoints |
| ChannelHost SQLite store | non-secret attachment settings, credential references, provider cursors, durable ingress outbox pending canonical Gateway acknowledgement |
| Manager desired-state ledger | accepted/reconciled workspace topology and retry state |
| UpdateService JSON stores | atomically replaced, disk-flushed update/catalog state and operation history; malformed persisted JSON fails visibly rather than resetting state |
| Agent workspace files | plaintext attachments and workspace content under the per-agent workspace; database rows retain metadata/references; encrypted host storage is a deployment requirement |

Exact shared and per-agent tables are defined by `baseline.current.sql` and `baseline.agent.current.sql`. Empty databases receive those baselines. Existing databases receive registered compatible upgrades, then fail closed on unknown drift.

Gateway schema changes are versioned. The `gateway-2026-10-23.1` to `.2` migration rebuilds the Telegram-era schedule, channel-interaction, user, and approval tables into their canonical provider-neutral definitions while preserving rows. It is registered for the known `gateway-2026-10-10.1`, `gateway-2026-10-19.1`, and `gateway-2026-10-23.1` markers, and also runs when an older marker carries the recognized legacy columns/default. Unknown markers without that legacy schema signature fail closed. The migration disables SQLite foreign-key enforcement before beginning its transaction, rebuilds tables and advances the schema marker atomically, checks `PRAGMA foreign_key_check` before commit, and restores enforcement afterward. Non-empty legacy Telegram identity arrays stop migration for explicit recovery rather than being discarded.

`agent_tool_surface_current` stores tool names, descriptions, and JSON schemas for the current tooling fingerprint. A miss lists MCP schemas and skill names without constructing session-bound functions; concurrent first turns share that listing. Each waiter then hydrates proxies for its own tool context. Incomplete MCP lists are not persisted.

## Commit before projection

Provider, model, agent, session, run, and memory command paths persist authoritative state before mutating their `GatewayState` projection or publishing downstream desired state. Session/message append updates session metadata and inserts all messages in one immediate transaction. Agent definition and thin shared registry updates use an attached-database transaction so both databases commit together. Skill deletion first persists replacement definitions for every affected agent, then deletes the Skill authority, so restart cannot restore stale attachments to a missing Skill.

`GatewayState` is a singleton projection/cache reconstructed from SQLite. Providers, models, agents, MCPs, Skills, users, and settings hydrate when it is resolved; heavier runtime collections load lazily under locks. `SqlitePlatformConfigurationProvider.GetCurrent()` reads current database rows on each call rather than returning an authoritative memory snapshot.

## Cross-store boundaries

Some workflows cannot be one SQLite transaction:

- Usage facts first enter durable `kautuka.db` staging, then transfer idempotently to `kautuka-usage.db` and delete staging. This is at-least-once transfer with run-ID deduplication. Each completed model turn is a distinct fact (`run-{guid}`); `UNIQUE(run_id)` must not reuse the Narad `runId`, or HITL continuation would drop earlier turns.
- Manager desired-state publication happens after Gateway commits configuration. Manager persists acceptance and reconciles independently.
- Conversation uploads create a durable staging row before writing bytes. Asset metadata insertion and staging-row deletion commit together; immediate failure cleanup and startup reconciliation remove only files identified by interrupted staging records, leaving unrelated workspace files untouched.
- Runtime event and its outbox are atomic within the per-agent store; external delivery occurs after commit.
- Rooted execution events are committed first in the root agent store. A leased outbox may mirror nested events into the source/child agent store later.
- HITL/AAAT continuity spans `kautuka.db` approval/wait/invocation/job state and per-agent run/checkpoint/event state. There is no cross-store transaction; durable IDs, conditional claims, leases, and reconciliation provide restart safety.

## Concurrency

Repositories use explicit transactions for aggregates and single-statement SQLite autocommit for bounded topology mutations, plus conditional updates, uniqueness constraints, busy timeouts, and scoped locks. Continuation/outbox workers claim leases before side effects. Projection and restart tests cover important failed-write boundaries, including Skill attachment removal and interrupted asset uploads.

`config_snapshots` is an active immutable revision store used by runtime configuration capture and lookup; live normalized tables remain the current configuration projection. Provider secrets have no Gateway schema or startup hydration path and remain exclusively under CredentialsHost authority.
