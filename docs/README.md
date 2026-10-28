# Kautuka developer documentation

This directory explains the system that the current code implements. Code, schemas, protocol definitions, and tests are authoritative; documentation is a maintained guide to those sources, not a competing specification.

## Start here

- [User getting started guide](getting-started.md) — installation, first run, and troubleshooting.
- [Developer guide](development/README.md) — clean checkout, launch, tests, and contributions.

Read these in order:

1. [System architecture](architecture/system.md) — processes, boundaries, and a request walkthrough.
2. [Execution model](architecture/execution.md) — canonical events, HITL, AAAT, recovery, and stop semantics.
3. [Data model](architecture/data.md) — authoritative stores, projections, and transaction boundaries.
4. [Security model](architecture/security.md) — trust boundaries, enforced controls, and current gaps.
5. [Frontend architecture](architecture/frontend.md) — React, Narad Protocol client, workspace rendering, authentication, and onboarding ownership.
6. [Brand system](architecture/branding.md) — canonical name capitalization, wordmark, mark, gradient, icon variants, and cross-system audit.
7. [Development workflow](development/workflow.md) — setup, validation, and repository commands.
8. [Browser completion checklist](development/browser-verification.md) — mandatory smoke, boundary matrices, and evidence required before marking browser work done.
9. [Channel completion checklist](development/channel-verification.md) — automated and live-provider validation required before marking channel work done.
10. [Checklist classification mapping](development/checklist-classification.md) — classification of browser/channel checklist items.
11. [Channel resilience verification](development/channel-resilience.md) — deterministic restart, partition, concurrency, soak, and replay evidence.
12. [Narad release gates](development/narad-release-gates.md) — performance, security, and dependency audit gates for Narad cutover.
13. [Channel runtime operational runbooks](operations/channel-runtime-runbooks.md) — operator procedures.
14. [Historical alpha notes](history/alpha-notes.md) — a short summary of early test builds; environment-specific records were removed.
15. [Historical channel and workflow notes](history/channel-workflows.md) — a sanitised summary of the early channel and workflow implementation.
16. [Telegram channel UX](operations/telegram-channel-ux.md) — single-chat agent/session routing and legacy topic retirement.
17. [Agent workspace](operations/agent-workspace.md) — session labels, ordering and timestamp display.
18. [Third-party release compliance](operations/third-party-release-compliance.md) — component inventory, license notices, upstream source references, and per-release SBOMs.

## Authority map

Each concern has one canonical explanation. Other pages link to it rather than restating it.

| Concern | Canonical document | Primary code anchors |
|---|---|---|
| Process topology | [System architecture](architecture/system.md) | `KautukaHostNetworkPolicy`, host `Program.cs` files |
| Runs, events, HITL, AAAT | [Execution model](architecture/execution.md) | `Domain/Execution`, `Domain/Hitl`, `RuntimePlane` |
| Storage and transactions | [Data model](architecture/data.md) | SQLite baselines and repositories |
| Authentication, authorization, credentials, containment | [Security model](architecture/security.md) | auth endpoints, CredentialsHost, Manager runtime |
| Browser and Narad ownership | [Frontend architecture](architecture/frontend.md) | `AppRoutes`, `AgentPageV2` (workspace), Narad endpoint mappings |
| Agent workspace session list | [Agent workspace](operations/agent-workspace.md) | `AgentPageV2`, `SessionTitleService` |
| Visual identity and branded assets | [Brand system](architecture/branding.md) | `BrandMark`, canonical SVG/PNG/ICO, installer, Windows Monitor tray, sites project |
| Interaction protocol and product UX | [Agent Interaction Protocol (Narad)](reference/agent-interaction-protocol.md) | Gateway Narad projection, `/api/narad/v1`, `@devkrity/narad`, `@devkrity/narad-react` |
| Service responsibilities | [Service reference](reference/services.md) | each service entry point and protobuf surface |
| Channel/runtime ownership and security trace | [Channel runtime ownership map](reference/channel-runtime-ownership.md) | Gateway runtime, ChannelHost, CredentialsHost, projection and simulator owners |
| Ports, paths, and databases | [Runtime reference](reference/runtime.md) | shared hosting policies and schema baselines |
| Wire contracts | [Protocol reference](reference/protocols.md) | protobuf files and Gateway endpoint mappings |
| Agent Interaction Protocol (RC1) | [Agent Interaction Protocol (Narad)](reference/agent-interaction-protocol.md) | Standalone authority, replay, lifecycle, heartbeat, and targeted client-execution contract |
| Canonical runtime event catalogue | [Runtime event catalogue](reference/runtime-events.md) | Narad authority classes, public delivery rules, and conformance coverage |
| External Channel Gateway consumers | [Channel Gateway consumer contract](reference/channel-gateway-consumer-contract.md) | semantic contracts, ChannelHost protobuf, reference consumer |
| Engineering rules | [Engineering standards](development/standards.md) | analyzers, architecture tests, pre-commit gates |
| Narad release gates | [Narad release gates](development/narad-release-gates.md) | Narad performance/security suites, dependency audit, client corpora |
| Auditability ratchet decisions | [Auditability decisions](architecture/auditability-decisions.md) | conceptual-owner and acceptance architecture tests |
| Browser completion testing | [Browser completion checklist](development/browser-verification.md) | application routes, Kautuka workspace chat, Gateway runtime and service logs |
| Channel completion testing | [Channel completion checklist](development/channel-verification.md) | setup UX, provider transports, Channel Gateway security, lifecycle, and evidence |
| Channel resilience verification | [Channel resilience verification](development/channel-resilience.md) | owned acceptance profiles, standalone simulator faults, replay evidence |
| Extending the platform | [Extension guide](development/extensions.md) | provider, tool, MCP, skill, and channel registrations |
| Installation | [Installation](operations/installation.md) | installer and release scripts |
| Releases and updates | [Releases and updates](operations/releases.md) | workflows and UpdateService |
| Workflow lifecycle API | [Workflow lifecycle API](reference/workflow-lifecycle-api.md) | archive, restore, delete, and run retention |
| Historical alpha record | [Historical alpha notes](history/alpha-notes.md) | early release context |
| Historical channel and workflow notes | [Historical channel and workflow notes](history/channel-workflows.md) | early channel behavior and workflow review |
| Diagnostics | [Troubleshooting](operations/troubleshooting.md) | health endpoints and telemetry wiring |

## Decisions and active plans

- [Single-instance channel concurrency plan](plans/single-instance-channel-concurrency.md) — polling isolation and durable delivery stages.
- [ADR 0001: Podman runtime](adr/0001-podman-runtime.md)
- [ADR 0002: Canonical runtime events](adr/0002-canonical-runtime-events.md)
- [ADR 0003: Secure Channel Gateway boundary](adr/0003-secure-channel-gateway-boundary.md)
- [ADR 0004: Channel Gateway productization gate](adr/0004-channel-gateway-productization.md)
- [ADR 0005: Channel runtime projection and verification](adr/0005-channel-runtime-projection-and-verification.md)
- [ADR 0006: AG-UI interoperability boundary and one product UX](adr/0006-ag-ui-interoperability-and-product-ux.md) — **superseded**; retained for historical rationale only
- [ADR template](adr/template.md)
- [Manifest host activation](plans/manifest-host.md)
- [Package admission and isolation](plans/package-admission.md)
- [Core trust-boundary hardening](plans/trust-boundary-hardening.md)
- [Secure Channel Gateway and native channel experience backlog](plans/secure-channel-gateway-backlog.md)
- [Channel runtime hardening and acceptance automation backlog](plans/channel-runtime-hardening-backlog.md)
- [Signed product backlog](plans/signed-backlog.md)
- [Agent workflows](plans/agent-workflows.md)
- [Historical channel and workflow plan](history/beta-channel-workflows.md)
- [Narad as one protocol (Kautuka-first, extractable)](plans/narad-one-protocol.md)
- [Governed workflow platform (SMB leader)](plans/governed-workflow-platform.md)
- [Business-process orchestration backlog](plans/business-process-orchestration-backlog.md)
- [Memory governance backlog](plans/memory-governance-backlog.md)
- [Update orchestration hardening backlog](plans/update-orchestration-hardening.md)
- [Windows appliance residency](plans/appliance-residency.md)
- [Appliance hardening](plans/appliance-hardening.md)

Only active future work belongs in `plans/`. Completed plans are deleted after their durable decisions are incorporated into architecture or ADRs; Git history retains the implementation record.

## Security review records

These dated records describe findings at the time of each review. The security model above documents current behavior.

- [Security review, 9 October 2026](development/security-review-2026-10-09.md)
- [Branch security review, 10 October 2026](development/security-branch-review-2026-10-10.md)
- [Runtime security review, 9 October 2026](development/security-runtime-review-2026-10-09.md)
- [Second source security review, 11 October 2026](development/security-source-round2-2026-10-11.md)
- [Security changes validation, 12 October 2026](development/security-validation-2026-10-12.md)
- [Microsoft sign-in routing and validation, 13 October 2026](development/microsoft-sign-in-routing.md)
- [Security validation, 26 October 2026](development/security-validation-2026-10-26.md)

## Documentation rules

- Derive behavior from executable code, schemas, protocols, and tests. Do not copy comments or old prose as evidence.
- Put a fact in one canonical document. Link to it elsewhere.
- Describe current behavior separately from desired behavior.
- Prefer backward-compatible changes. API, event, configuration, and persisted-data changes require compatibility analysis and migrations; breaking changes require maintainer approval and release notes.
- Run `npm run docs:check` before committing. It rejects broken links, orphan documents, unclassified folders, and plans without ownership metadata.
