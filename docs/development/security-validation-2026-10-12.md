# Security changes validation, 12 October 2026

This record covers the pending security changes and reconciliation cleanup on `fix/security-review-2026-10-09`. It records source checks, automated results, and local Windows/rootless Podman observations separately.

## Automated checks

- Complete solution restore and build passed with zero warnings and errors.
- Final backend gate: 1,792 passed, one skipped. The skip concerns Unix group socket permissions on Windows.
- Separate API flow suite: two passed. This suite exercises catalog creation, local Ollama chat, and credential-key filtering in an isolated Gateway test host.
- Frontend: 321 tests passed; lint, type checking, and production build passed.
- Full solution format verification, documentation validation, Narad validation, and Git whitespace checks passed.
- The pending patch secret scan found no leaks. It used the existing cached Gitleaks image with networking disabled, a read-only repository mount, and an isolated Git index. The real staging area was not changed.

Existing suites cover workspace ownership and path identity, cross-user memory and asset access, MCP cache authorization and credential filtering, skill confinement, credential recovery, owner permissions, certificate reload, proxy route confinement, and approval/download behavior.

Added failure coverage exercises sandbox, connector, and sidecar reconciliation with inspection timeout, unavailable inspection, malformed JSON, incomplete JSON, and failed removal. No replacement commands are permitted in those cases. A recovery test distinguishes unavailable observation from confirmed absence. Eight concurrent skill writers also verified that case-variant IDs cannot both persist.

## Defects identified during validation

### Slack simulator endpoint

The new Slack WebSocket destination guard rejected the standalone simulator's configured loopback endpoint. The correction admits only that exact endpoint when the already restricted Development/Test simulator profile is selected. Official transport retains its Slack-owned secure WebSocket restriction. Endpoint-boundary tests and the real simulator connection/reconnect/acknowledgement test passed.

### Podman capability representation

Live events showed repeated agent container replacement despite healthy readiness reports. `HostConfig.CapDrop` expanded `ALL` into individual default capability names. The existing drift check required the literal `ALL` and therefore rejected the containers it had created.

The corrected comparison uses Podman's resolved effective and bounding capability sets when present; both must be empty. The local inspection serialized both as null, and `/proc/1/status` confirmed zero capability masks. Regression cases cover null and empty arrays, nonempty effective/bounding sets, and added capabilities. [Podman container inspection reference](https://docs.podman.io/en/latest/markdown/podman-container-inspect.1.html).

An initial broad test run had two timeouts. The Narad stream test passed on isolation and subsequent full runs. The channel timeout reproduced independently and was resolved by the simulator endpoint correction. The separate API chat flow initially completed without assistant text; the local model answered a direct probe, and a diagnostic rerun and the final unmodified API suite passed. This intermittent result remains recorded rather than being treated as a deterministic defect or hidden by changing assertions.

## Local runtime observations

- The freshly built stack started and restarted successfully; Gateway and Manager reported healthy with all desired agents ready.
- After the capability comparison correction, the six agent container IDs remained unchanged over a 126-second observation, with zero agent create/remove/restart events.
- A temporary live check loaded the persisted desired state, checked the expected fingerprint and drift before acting, and performed three repeated ensures for each of six existing sandboxes. All 18 ensures reused their container IDs without drift notes. The temporary test source was removed afterward.
- Successful Podman info probes while the stack ran took 366-406 ms; ps took 337 ms. A later info probe took 385 ms.
- OpenObserve health and synthetic log ingestion returned HTTP 200.
- Package proxy health reported database/storage OK; package metadata delivery succeeded.
- Installer headless bootstrap omitted its action token. Missing/wrong action tokens and a foreign Host header returned 403. The valid token closed the test instance with 200. No install/uninstall operation ran.

## Authenticated browser checks, 13 October 2026

The user signed in as admin in the in-app browser and as a second non-admin user in a separate Edge session.

- The admin dashboard showed Gateway healthy and Agents Ready, including the expected on-demand connector notes. The non-admin dashboard omitted administrative controls and those notes.
- A disposable Roller conversation requested `roll-dice` with `sides: 20`, paused for approval, executed after approval, and returned 18. A second standard d20 request was denied; history showed Denied, the assistant reported denial, and logs showed denial continuation without a second skill execution. Existing expired approvals appeared as Expired without action buttons.
- Disposable text and HTML workspace uploads succeeded. The first file chooser operation took approximately 400 seconds in the browser control; a repeat HTML upload completed in about six seconds. This does not establish a server upload performance defect. Clicking HTML download did not expose a preview tab in the in-app browser, so live sandboxed preview rendering remains unverified.
- The non-admin account did not see Roller in its agent catalog. Direct navigation to the admin's Roller session did not reveal the conversation. Direct navigation to the admin's disposable Secretary session also did not reveal that session. Follow-up inspection confirmed the two accounts have distinct, user-owned Secretary agents (`agent-1791271506742` and `agent-1791557887584`); the earlier description of Secretary as shared was incorrect. The second account had no approval history and no All approvals control.
- Admin Secretary's existing configuration disables its sandbox. File controls correctly reported this and were disabled. No genuinely shared agent was available for a full-stack browser file isolation check.
- Admin mailbox connectors displayed existing signed-in status. The second user's Microsoft connector initially required sign-in. Microsoft OAuth initiation reached Microsoft's account sign-in page with PKCE; the user completed sign-in and consent. The callback returned to Kautuka, the second user's connector showed Signed in, and that state persisted after a reload. No mailbox tools were invoked.
- A Podman info probe during these checks completed in 1,143 ms. The stack remained running.

No implementation changes were made during these browser checks. Two disposable workspace files and the disposable sessions remain available for review. Existing approvals were not changed; the second user's previously disconnected Microsoft mailbox was connected with the user's consent.

### Follow-up file and sharing checks

- The existing `SystemAgentWorkspacesAreSeparateForEachUser` and `DeleteEndpointAllowsSameAttachmentNameInAnotherUsersSystemWorkspace` tests were rerun: two passed. These use an isolated Gateway test host, authenticated users, a genuinely system-scoped agent, and real temporary filesystem workspaces. They verify listing isolation and that deleting the member's same-named file preserves the admin's attachment. They do not exercise the running stack's Podman mounts.
- A disposable sandbox agent (`agent-1791865535956`, Disposable file preview validation) and empty session were created for the second user. Edge's upload tool stalled and then refused file access because the extension's file URL permission was disabled; no browser permission was changed. This temporary agent remains available for review.
- A temporary loopback browser harness loaded the current `openDownloadedFile.ts` source, stripping TypeScript annotations only. Edge rendered the synthetic HTML in the helper's popup. The iframe's `sandbox` attribute was the empty string, its body's script marker remained absent, and the script's appended text was absent. This verifies the actual helper's rendering and script blocking in a browser, separately from the authenticated download route. The harness and its process were removed after inspection.
- Agent sharing has no UI control. The form has no scope editor, the save payload has no sharing/scope field, and `GatewayAgentUpsertRequest` accepts none. `SaveAgentAsync` preserves an existing scope and assigns `user` to a new agent. Authorization admits the owner for user-scoped agents or everyone for system-scoped agents; there is no per-user grant model in this path. The Users tab's "Capabilities and object grants" label promises controls it does not provide.

## Remaining verification limits

- Full-stack shared-agent workspace file isolation remains unverified because no genuine shared sandbox agent is configured and no sharing UI exists. Authenticated shared-agent file tests passed in the isolated test host. HTML script blocking passed using the actual helper in Edge; authenticated upload-to-preview completion in Edge remains blocked by extension file access. Microsoft mailbox OAuth completed live; Google and external-login OAuth were not exercised live.
- This run does not establish Linux/SYSTEM-account deployment behavior or installer installation/uninstallation behavior.
- The runtime observation is a bounded smoke check, not a long soak or a complete 30-minute periodic audit interval.
- Local startup also reported unavailable update-feed service and invalid pre-existing persona-profile seed data. These did not block readiness and were not repaired as part of this validation.

The first commit attempt was blocked by a five-second timeout in the unchanged frontend WorkflowDiagram test. Its two tests passed immediately in isolation (1.61 seconds for the isolated run). Backend hooks had passed 1,792 tests with the expected single Windows skip. The normal hooks were retried without changing assertions, timeouts, or hook configuration.

## PR Linux CI follow-up, 13 October 2026

PR #38 exposed Windows assumptions in the new filesystem test fixtures. Eight tests
attempted to launch `cmd.exe` on Linux, and two expected Windows-specific filename
alias/device rules on a Linux filesystem. Link fixtures now create Windows junctions
and hard links with `mklink`, and Unix symbolic links and hard links with `ln` using
separate process arguments. Link confinement assertions run on both platforms.
Windows device-name endpoint assertions are explicitly Windows-only; the mixed
workspace and journal tests assert the respective platform filename semantics.
No production security policy or CI gate was changed for this correction.

## Residual fixes validated on 2026-10-13

- Gateway shutdown and desired-state rehydrate explicitly require an authenticated admin through the AdminManagement policy, in addition to their existing loopback restriction. Anonymous and ordinary-user requests are denied before side effects; a loopback admin can invoke both operations, and a remote admin remains denied.
- WorkspaceFile now rejects links during the actual native lookup. Linux uses openat2 relative to an open filesystem-root handle with BENEATH, NO_SYMLINKS and NO_MAGICLINKS. Windows uses NtCreateFile relative to an open volume-root handle with DONT_REPARSE. Handle metadata and link counts are validated before truncation or reading. Neither implementation falls back to an unsafe path open.
- Agent file uploads apply Unix permissions through the open file handle, avoiding a later permission change through a replaceable path.
- Regression tests replace a parent directory after path authorization and verify that reads, creation and overwrite cannot access an outside sentinel. They also cover hard-link overwrite without outside truncation, ordinary read/create/overwrite, unrelated directory operations, and concurrent Linux parent/symlink swaps.
- The final Windows integration run passed 1,561 tests, with zero failures and two platform-specific skips (Unix socket mode and Linux race validation). The Linux/WSL focused run passed 87 tests, with zero failures and one Windows-device-name skip. Build completed with zero warnings and errors.
- Secure Linux file access currently supports x64 and arm64 and requires openat2 (Linux 5.6 or newer). Unsupported platforms or kernels fail closed. Validation used the local Windows installation and Ubuntu2404 under WSL, kernel 6.18.54.1.
- This closes the identified WorkspaceFile check/open race; it is not a claim that every other filesystem operation has been audited. The Windows desktop host's unauthenticated loopback graceful-shutdown request is rejected by the Gateway's AdminManagement policy, then the host uses its process-termination fallback. This is not an authorization bypass; wiring the host to an authenticated graceful-shutdown flow remains a separate engineering follow-up.
