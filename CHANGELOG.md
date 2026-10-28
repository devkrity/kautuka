# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog. This repository follows Semantic
Versioning for named cuts. `0.1.20` is the current product version; earlier
`0.1.0` insiders tags were local test artifacts and are not a supported
upgrade path.

## [Unreleased]

### Security

- Slack privacy hardening: DM-only, single-owner workspace binding, restricted setup scopes, and owner-pinned outbound delivery.

## [0.1.20] - 2026-10-27

### Licensing

- Kautuka's first public release is licensed under Apache-2.0.

### Added

- Product version `0.1.20`, the first non-prerelease cut; Windows assembly version is `0.1.20.0`.
- Narad packages `0.1.0` from npm, carrying forward Kautuka's Narad patch.
- Workspace agent tree, session origin labels and timestamps, and persona/runtime settings that persist across Gateway restarts.
- Slack setup now opens a prefilled app manifest, checks the two token types, pairs the owner through a short-lived DM code, and learns the default DM destination without asking for Slack IDs.
- Built-in Microsoft 365 mail connector. Owners use Sign in with Microsoft on the Connectors page. Sign-in is authorization code with PKCE through `https://auth.kautuka.io`. `mail_send` starts as Ask me first and the owner can change it. Refresh tokens stay in CredentialsHost.
- Mailbox sign-in has two modes. A source checkout uses the developer's own Google or Microsoft app and `http://127.0.0.1:<port>/api/connectors/oauth/callback`. The release build sets `Kautuka:ConnectorAuth:Host` to `https://auth.kautuka.io`, which inserts the client id. The PKCE verifier stays on the appliance.
- The release image packs `kautuka-google-mail` next to Microsoft mail. Hosted Microsoft refresh requires the same kind of registered sign-in key Google already requires.

### Removed

- Retired the legacy `kautuka` command-line/TUI tool from the solution, release assets, and active integration tests. Its source is retained under `src/archive/legacy-tui/` for historical reference.

## [0.0.1-alpha.1] - 2026-08-30

First named alpha of the Kautuka appliance. Source-first: do not treat
unsigned installer or update feeds as trusted. See
[alpha notes](docs/history/alpha-notes.md).

### Added

- Named platform run clocks (model timeout, max run duration, HITL expiry) with per-agent Runtime overrides, HITL on-expiry actions (`inform` / `retry` once / `abort`), and a 300s catalog default for provider HTTP when `http_timeout_seconds` is unset.
- Explicit provider `http_timeout_seconds` settings in the admin UI and onboarding flow. New catalog rows default to 300 seconds; existing stored provider timeouts are unchanged.
- Opt-in structured agent planning as an internal preflight phase in the normal run lifecycle, with persisted `plan` versions, plan-review pause/resume, revision loops, planned-vs-actual reconciliation, and trace-continuous approval/revision resumes.

### Changed

- Nested AAAT child HITL is awaited inside the parent tool invoke so only the real child result is returned to the outer session (no `pending_approval` function result).
- Max run duration now bounds active child work only; nested HITL wait pauses that clock, and a parked `waiting_on_child` invocation is not marked failed if the live waiter dies.
- AAAT child max-run remaining is restored from the wait-state clock checkpoint after nested HITL and across process death (same envelope as outer runs; the live orchestrator budget rebinds from that checkpoint instead of restarting at zero).
- Gateway background workers treat provider `HttpClient` timeouts as retryable job failures instead of stopping the host.
- Browser nested AAAT approvals no longer schedule durable continuation while an in-process agent-tool waiter owns the request, and parent re-entry ignores outer approval waits that are not durable `waiting_on_child` pauses.
- Nested AAAT interrupt handling drains through `run.paused` before awaiting approval, and durable enqueue is skipped when a nested link exists without a parent `waiting_on_child` pause (store-backed ownership, not only in-memory `IsWaiting`).
- Approval resume for `invoke_agent_*` now always loads the live MAF session like ordinary tools (no wait-state session override), so MAF 1.19 approval binding can invoke AAAT after approve.
- Microsoft Agent Framework packages updated from 1.12.0 to 1.19.0, with Microsoft.Extensions.AI lockstep 10.7.0 to 10.9.0 and DependencyInjection.Abstractions 10.0.11.
- Repository structure cleanup for open-source publication.
- Standardized root governance files and contributor workflow.
- Gateway now classifies provider initialization transport and timeout failures centrally during runtime construction and reports them as early provider-unavailable failures instead of surfacing a generic upstream cancellation.
- Automatic session compaction now uses an 80% threshold fallback when `autoCompactThresholdPercent` is missing, and the current user message is included in the pre-run context estimate before the compaction decision is made.
- Recoverable memory-embedding ranking failures now degrade to non-embedding ranking, and memory ranking respects the `memory.embeddings_enabled` setting.
- Memory fact extraction now uses a single prompt that asks the model to emit only durable, record-worthy facts, with stronger negative examples to filter out one-off requests, temporary states, and task-specific chatter.
- Linux appliance install/update flow now re-normalizes persisted sidecar data permissions so rootless sidecars can reopen existing state after an update.
- Release packaging now stages Manager runtime sidecar assets and the built-in `kautuka-web-tools.mcpb` bundle into the platform payload consistently for local and release builds.
- UpdateService now treats the expected staged-updater restart handoff termination (`SIGTERM` / exit code `143`) as a successful self-handoff during apply.
- Channel settings are now provider-schema-driven through ChannelHost instead of a Telegram-only Gateway contract, and Slack ingress now filters bot-authored echoes before they can loop back into chat execution.
- Chat now renders approved plans as collapsed summaries, clears stale plans while a revision is being reviewed, and keeps execution evidence in the Narad workspace surface.
- Product version is `0.0.1-alpha.1`. Windows installer assembly version stays numeric `0.0.1` because Win32 `System.Version` cannot parse prerelease labels.
