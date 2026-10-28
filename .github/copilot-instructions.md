# Kautuka repository instructions

Work as a principal engineer on a .NET 10, React, SQLite, Narad, and Podman appliance. Derive behavior from executable code, schemas, protobuf definitions, endpoint composition, and tests. Comments and documentation are navigation aids, not authority.

## Read first

1. `docs/README.md`
2. `docs/development/standards.md`
3. The canonical architecture page for the boundary being changed

Do not use deleted/superseded plans or reconstruct legacy behavior from Git history unless the task explicitly asks for it.

## Runtime boundaries

- Gateway owns public HTTP, authentication, orchestration, canonical runtime events, HITL, AAAT, configuration, and Narad projection.
- CredentialsHost owns encrypted credential persistence and authorization.
- Manager owns desired workspace state, path authority, Podman sandboxes, and connector execution.
- ChannelHost owns Telegram and Slack transport adapters.
- UpdateService owns release/catalog discovery, activation, and rollback.
- WebHost serves the SPA and proxies `/api` to Gateway.
- Internal service protocols are proto-first and use generated clients.

## Non-negotiable engineering rules

- Make forward-only changes. Do not add legacy schemas, compatibility shims, dual read/write paths, or deprecated contracts without explicit authorization.
- Preserve Native AOT and trimming. Avoid runtime code generation, reflection-dependent persistence/serialization, assembly scanning, and service-location patterns.
- Persist authoritative state before returning success or updating its projection. Model cross-store delivery explicitly with durable staging/outbox/leases.
- Propagate cancellation unless durable ownership deliberately survives the caller; bound payloads, queues, retries, and resources.
- Keep process boundaries least-privilege and secrets out of configuration projections, argv, logs, and tool results.
- Use source-generated JSON/protobuf contracts and explicit parameterized SQLite commands.
- Keep files/classes focused by semantic responsibility; do not create forwarding facades or god classes.

## Execution and browser

- Per-agent canonical runtime events and root sequence own run-visible history.
- Approval resolution commits a decision; it does not mean resumed or completed.
- AAAT must retain exact parent run/tool/root lineage and exactly one terminal parent tool result.
- `/api/narad/v1` is the browser chat runtime. Gateway is the sole projection owner; `@devkrity/narad` / `@devkrity/narad-react` reduce and render the stream.
- Do not add a second transcript, ordering, approval, or reconnect model in React.
- Use TanStack Query for ordinary server state. Use Zod where an external payload is runtime-validated; do not claim validation where only TypeScript types exist.

## Validation

- Behavior changes require focused tests plus the applicable restart, crash, negative-security, contract, Native AOT, and browser evidence.
- Update the one canonical documentation page and delete completed plans.
- Run `npm run docs:check` and the repository pre-commit gate.
