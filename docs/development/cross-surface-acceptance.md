# Cross-surface acceptance mapping

`src/tests/channel-acceptance` is the typed Playwright workspace for the channel-runtime hardening backlog. It starts test-owned processes unless an explicitly configured execution reuses a stack. It never enables a product authentication bypass or a production simulator path.

Status is deliberately narrow: **Automated** means a concrete Playwright scenario exercises the stated behaviour; **Contract** means a fast source/evidence invariant only; **Resilience-owned** means a direct-child owned-stack Playwright test outside the aggregate acceptance suite; **Planned** means it is not yet an executable acceptance claim.

## Executable acceptance scenarios

| Backlog area | Status | Concrete scenario |
|---|---|---|
| Browser load, navigation, direct reply | Automated | `CRH-030-BROWSER-SMOKE` |
| Owner setup, validation, activation, verification, suspend/recovery and credential replacement | Automated | `CRH-031-TELEGRAM-SETUP`, `CRH-031-SLACK-SETUP` |
| Permanent channel remove | Automated | `CRH-031-TELEGRAM-REMOVE`, `CRH-031-SLACK-REMOVE` |
| Provider-neutral inbound/outbound transport | Automated | `CRH-032-PROVIDER-NEUTRAL-CONVERSATION` |
| Provider command / file-on-message / stream-edit evidence (phase 1) | Automated | `CRH-032-COMMAND-INGRESS`, `CRH-032-FILE-ON-MESSAGE`, `CRH-032-STREAM-EDIT-EVIDENCE` |
| Cross-surface approval continuation and attach timing | Automated | `CRH-034-CROSS-SURFACE-SYNCHRONIZATION`, `CRH-034-CHAT-APPROVALS-CHANNEL-CONVERGENCE`, `CRH-034-ATTACH-BEFORE-REQUEST`, `CRH-034-ATTACH-WHILE-PAUSED`, `CRH-034-ATTACH-DURING-CONTINUATION`, `CRH-034-ATTACH-AFTER-TERMINAL` |
| Nested AAAT lifecycle | Automated | `CRH-035-NESTED-SEQUENTIAL-MIXED-RESOLUTION`, `CRH-035-NESTED-CHILD-CANCEL`, `CRH-035-NESTED-CHILD-STOP`, `CRH-035-NESTED-BROWSER-RECONNECT-AT-CHILD-PAUSE`, `CRH-035-SAME-CHILD-CONCURRENCY` |
| Nested AAAT process restart at child pause | Resilience-owned | `CRH-035-NESTED-GATEWAY-RESTART-AT-CHILD-PAUSE`, `CRH-035-NESTED-CHANNELHOST-RESTART-AT-CHILD-PAUSE` |
| Snapshot, multi-prompt, compact/rename/archive | Automated | `CRH-036-SNAPSHOT-RECONNECT-REPLAY`, `CRH-036-MULTI-PROMPT-RECONNECT-REPLAY`, `CRH-036-COMPACT-RENAME-ARCHIVE` |
| Security boundary | Automated | `CRH-037-SECURITY-BOUNDARY` |
| Dependency outage recovery | Resilience-owned | `CRH-037-DEPENDENCY-OUTAGE-RECOVERY` |
| Files safety and upload listing | Automated | `CRH-038-FILES-CONTENT-SAFETY`, `CRH-038-FILE-UPLOAD-REPLAY` |
| Operational faults | Automated | `CRH-039-OPERATIONAL-DISCONNECT`, `CRH-039-OPERATIONAL-RATE-LIMIT`, `CRH-039-OPERATIONAL-DUPLICATE` |
| Checklist migration | Contract | [checklist-classification.md](checklist-classification.md) |
| Evidence / partition / concurrency / triage contracts | Contract | `evidence.spec.ts`, `scenario-contract.spec.ts`, `checklist-classification.spec.ts`, `partition-matrix.spec.ts`, `concurrency-matrix.spec.ts`, `failure-triage.spec.ts` |
| Skill-approval process restart | Resilience-owned | `CRH-050-APPROVAL-PENDING-RESTART`, `CRH-050-CHANNELHOST-APPROVAL-PENDING-RESTART`, `CRH-050-SIMULATOR-APPROVAL-PENDING-RESTART` |
| Restart matrix baseline | Resilience-owned | `CRH-050-RESTART-MATRIX` |
| Light soak envelope | Contract | `CRH-053-LIGHT-SOAK` (~200 events/min and 20 concurrent; absolute correctness evidence) |
| Ownership / event catalogue / runbooks | Docs + architecture tests | [channel-runtime-ownership.md](../reference/channel-runtime-ownership.md), [runtime-events.md](../reference/runtime-events.md) (Narad authority catalogue), [channel-runtime-runbooks.md](../operations/channel-runtime-runbooks.md) |
| Generalized attachment / snapshot-live redesign | Automated | `CanonicalDeliveryAttachment` owns cursor, snapshot/reopen, and terminal attach; CRH-034/036 remain the Playwright regression net |

## Deferred / blocked (revisit later)

| Backlog area | Reason |
|---|---|
| CRH-032 phase 2 inbound reactions / message-edit ingress | Requires ChannelHost poll/intent widening beyond phase 1 |
| Multi-hour soak and percentile SLOs | Light envelope approved; long-duration gates remain product-owned |
| M5 live-provider compatibility (CRH-070–073) | Requires isolated Telegram/Slack credentials (owner-supplied later) |
| Remaining manual checklist items (C-09, A-01–A-04, A-06, F-02–F-04, F-07–F-08, U-01–U-03, R-03, O-04–O-05) | Retained developer checks; not required to close the automated hardening claims above |

Live-provider checks remain a release-candidate compatibility activity and are not represented by simulator success.

## Isolated local model prerequisite

Owned acceptance starts from an empty Kautuka state root. Set `KAUTUKA_ACCEPTANCE_MODEL_NAME` to an actually installed local model (for this workstation, `gemma4:12b`), and optionally set `KAUTUKA_ACCEPTANCE_PROVIDER_BASE_URL`. The suite creates its own Ollama provider, model definition, admin, and fixture agents through normal product setup and authenticated public APIs; it never borrows a developer's existing Kautuka state.
