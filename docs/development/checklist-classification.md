# Checklist classification mapping (CRH-040)

Maps every item in [browser-verification.md](browser-verification.md) and [channel-verification.md](channel-verification.md) to one of:

- **Automated** — a named Playwright scenario or contract test exercises it
- **Manual** — retained developer checklist; not yet an executable acceptance claim
- **Live-provider** — requires disposable official-provider credentials (M5)
- **Blocked** — needs a product decision or capability that is not in the public surface yet

This document is the CRH-040 migration record. It does not invent new product behaviour.

## Browser checklist

| ID | Classification | Mapping |
|---|---|---|
| B-01 | Automated | `CRH-030-BROWSER-SMOKE` |
| B-02 | Automated | `CRH-030-BROWSER-SMOKE` |
| B-03 | Automated | `CRH-036-SNAPSHOT-RECONNECT-REPLAY`, `CRH-036-COMPACT-RENAME-ARCHIVE` |
| B-04 | Automated | `CRH-030-BROWSER-SMOKE`, `CRH-032-PROVIDER-NEUTRAL-CONVERSATION` |
| B-05 | Automated | Harness fails on unexpected browser console/network findings |
| B-06 | Automated | Harness fails on unexpected service findings |
| C-01 | Automated | `CRH-030-BROWSER-SMOKE` |
| C-02 | Automated | `CRH-033-BROWSER-APPROVE` |
| C-03 | Automated | `CRH-033-BROWSER-APPROVE`, `CRH-033-CHAT-DENY`, `CRH-033-CHAT-STOP-RUN` |
| C-04 | Automated | `CRH-035-NESTED-SEQUENTIAL-MIXED-RESOLUTION` |
| C-05 | Automated | `CRH-033-CHAT-DENY`, `CRH-033-CHAT-STOP-RUN` |
| C-06 | Automated | `CRH-035-NESTED-CHILD-CANCEL`, `CRH-035-NESTED-CHILD-STOP` |
| C-07 | Automated | `CRH-034-CHAT-APPROVALS-CHANNEL-CONVERGENCE`, `CRH-033-APPROVALS-APPROVE` |
| C-08 | Automated | `CRH-035-NESTED-BROWSER-RECONNECT-AT-CHILD-PAUSE`, `CRH-036-SNAPSHOT-RECONNECT-REPLAY`, `CRH-034-ATTACH-WHILE-PAUSED`, `CRH-034-ATTACH-BEFORE-REQUEST` |
| C-09 | Manual | Expanded tool-card inspection |
| C-10 | Automated | `CRH-035-SAME-CHILD-CONCURRENCY` |
| C-11 | Automated | `CRH-036-MULTI-PROMPT-RECONNECT-REPLAY`, `CRH-036-SNAPSHOT-RECONNECT-REPLAY` |
| A-01 | Manual | New Agent modal from chat |
| A-02 | Manual | Create agent + first conversation |
| A-03 | Manual | Configure Agent modal persistence |
| A-04 | Manual | Settings vs chat modal parity |
| A-05 | Automated | `CRH-037-SECURITY-BOUNDARY` ownership/auth negative probes |
| A-06 | Manual | Delete unreferenced test agent |
| F-01 | Automated | `CRH-038-FILE-UPLOAD-REPLAY` |
| F-02 | Manual | Drag/drop attachment-only turn |
| F-03 | Manual | `#path` file browser insertion |
| F-04 | Manual | Workspace file CRUD |
| F-05 | Automated | `CRH-038-FILES-CONTENT-SAFETY` |
| F-06 | Automated | `CRH-037-SECURITY-BOUNDARY` / `CRH-038-FILES-CONTENT-SAFETY` |
| F-07 | Manual | Image attachment replay |
| F-08 | Manual | Configure Agent editor is a centered modal |
| U-01 | Manual | Usage summary presence after runs |
| U-02 | Manual | Usage summary replay |
| U-03 | Manual | Usage after approval continuation |
| U-04 | Automated | `CRH-036-COMPACT-RENAME-ARCHIVE` |
| U-05 | Automated | `CRH-036-COMPACT-RENAME-ARCHIVE` |
| R-01 | Automated | `CRH-037-DEPENDENCY-OUTAGE-RECOVERY` |
| R-02 | Manual | CredentialsHost stop dependency |
| R-03 | Manual | Podman downstream failure |
| R-04 | Automated | `CRH-050-RESTART-MATRIX`, `CRH-050-APPROVAL-PENDING-RESTART`, `CRH-050-CHANNELHOST-APPROVAL-PENDING-RESTART`, `CRH-050-SIMULATOR-APPROVAL-PENDING-RESTART` |
| R-05 | Automated | `CRH-035-NESTED-GATEWAY-RESTART-AT-CHILD-PAUSE`, `CRH-035-NESTED-CHANNELHOST-RESTART-AT-CHILD-PAUSE`, `CRH-050-APPROVAL-PENDING-RESTART` |
| R-06 | Automated | `CRH-036-SNAPSHOT-RECONNECT-REPLAY`, `CRH-035-NESTED-BROWSER-RECONNECT-AT-CHILD-PAUSE`, `CRH-036-MULTI-PROMPT-RECONNECT-REPLAY` |
| R-07 | Automated | `CRH-037-DEPENDENCY-OUTAGE-RECOVERY` |

## Channel checklist

| ID | Classification | Mapping |
|---|---|---|
| P-01 | Automated | Build/preflight in owned acceptance stack |
| P-02 | Automated | `CRH-037-SECURITY-BOUNDARY` |
| P-03 | Automated | `CRH-037-SECURITY-BOUNDARY` |
| P-04 | Automated | `CRH-037-SECURITY-BOUNDARY` |
| P-05 | Automated | Credential boundary architecture/security tests |
| P-06 | Automated | `CRH-031-TELEGRAM-SETUP` |
| P-07 | Automated | `CRH-032-PROVIDER-NEUTRAL-CONVERSATION` |
| P-08 | Automated | `CRH-039-OPERATIONAL-RATE-LIMIT`, `CRH-039-OPERATIONAL-DUPLICATE` |
| P-09 | Automated | Channel semantic rendering unit/integration coverage |
| P-10 | Automated | `CRH-032-COMMAND-INGRESS`, `CRH-032-FILE-ON-MESSAGE`, `CRH-032-STREAM-EDIT-EVIDENCE` |
| P-11 | Automated | Simulator fault scheduling + adapter limit tests |
| P-12 | Automated | Channel Gateway reference consumer sample |
| L-01 | Automated | `CRH-031-TELEGRAM-SETUP` |
| L-02 | Automated | `CRH-031-TELEGRAM-SETUP` |
| L-03 | Automated | `CRH-031-TELEGRAM-SETUP` |
| L-04 | Automated | `CRH-031-TELEGRAM-SETUP` |
| L-05 | Automated | `CRH-032-PROVIDER-NEUTRAL-CONVERSATION` |
| L-06 | Automated | `CRH-032-COMMAND-INGRESS` |
| L-07 | Automated | `CRH-033-TELEGRAM-APPROVE`, `CRH-034-CHAT-APPROVALS-CHANNEL-CONVERGENCE` |
| L-08 | Automated | `CRH-032-FILE-ON-MESSAGE`, `CRH-032-STREAM-EDIT-EVIDENCE` |
| L-09 | Automated | `CRH-031-TELEGRAM-SETUP` |
| L-10 | Live-provider | Credential revoke + restart during pending work |
| L-11 | Automated | No-webhook / simulator transcript evidence hygiene |
| O-01 | Automated | Owned stack readiness waits + `CRH-037-DEPENDENCY-OUTAGE-RECOVERY` |
| O-02 | Automated | `CRH-039-OPERATIONAL-DISCONNECT`, `CRH-039-OPERATIONAL-RATE-LIMIT`, `CRH-039-OPERATIONAL-DUPLICATE` |
| O-03 | Automated | `CRH-050-RESTART-MATRIX` |
| O-04 | Manual | Certificate/delegation overlap rotation |
| O-05 | Manual | Installer process isolation |

## Explicitly deferred (revisit later)

| Item | Reason |
|---|---|
| Multi-hour soak / percentile SLOs | Light envelope (~200 events/min, 20 concurrent) approved; long gates remain product-owned |
| Live-provider compatibility matrix | M5 credentials/environments (CRH-070–073) |
| CRH-032 phase 2 inbound reactions / message-edit ingress | ChannelHost poll/intent widening |
