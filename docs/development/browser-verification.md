# Browser completion checklist

Use this checklist before marking user-visible or runtime-lifecycle work done. It is a developer verification aid, not a substitute for automated tests or release verification.

For the CRH-030 cross-surface automation mapping, see [cross-surface acceptance](cross-surface-acceptance.md). A mapped but not-yet-implemented scenario remains skipped and prevents a Full browser pass.

## Reporting rule

Name the scope accurately:

- **Regression pass** — only the scenarios affected by the change were run.
- **Full browser pass** — every mandatory scenario below and every applicable boundary matrix was run.

Never report a full pass when a scenario was skipped, blocked, or inferred from unit tests. Record every exclusion and its reason.

## Prepare the environment

1. Record the branch and commit under test.
2. Build the backend and production frontend from that commit.
3. Start CredentialsHost, ChannelHost, Manager, UpdateService, Gateway, and WebHost. Start Podman when the scenario uses tools, connectors, skills, or sandboxes.
4. Use a clean development data set or clearly identify the test records you create.
5. Sign in with the role required by the scenario. Do not reuse a session whose authorization state is uncertain.
6. Open browser developer tools. Keep the Console and Network panels available; monitor Narad session watch on `/api/narad/v1` when testing chat.

The root command `npm run launch` builds and starts the native stack. The browser is served at the WebHost address listed in the [runtime reference](../reference/runtime.md).

## Mandatory smoke

Run these checks for every user-visible change:

| ID | Check | Expected result |
|---|---|---|
| B-01 | Load the application and sign in or refresh an existing authenticated page. | The intended page loads without a global crash, authentication loop, or unexpected `401` response. |
| B-02 | Navigate through Dashboard, Agents, Approvals, and Settings. | Each page loads once, navigation is stable, and no stale dependency banner appears. |
| B-03 | Open an existing agent session, select another session, then reload the page. | The selected agent/session remains canonical; history is neither missing nor duplicated. |
| B-04 | Start a new conversation and send one ordinary prompt. | One user message and one final assistant answer appear; activity stops at completion. |
| B-05 | Inspect browser Console and failed Network requests. | No unexplained errors, failed runtime requests, or repeated unauthorized requests remain. |
| B-06 | Inspect Gateway and affected service logs. | No unhandled exception, critical error, background-service termination, or silent tool failure occurred. |

## Chat, HITL, and AAAT

Run this matrix when changing chat, Narad projection, canonical events, approvals, tools, continuation, or child agents. For Roller tests, use a fresh prompt such as `Use the Roller agent exactly once to roll a d<10-200>. Do not simulate the roll.`

| ID | Scenario | Expected result |
|---|---|---|
| C-01 | Direct agent response without a tool. | Streaming remains active until one final answer; no duplicate prompt or answer appears. |
| C-02 | Ordinary tool succeeds. | The exact tool card moves from Running to Done and its result appears once. |
| C-03 | Ordinary tool approval: Approve, Deny, Cancel, and Stop Run in separate sessions. | Each decision has a distinct terminal state, attribution message, and correct run outcome. |
| C-04 | AAAT: approve outer invocation and approve inner tool. | Parent and child tools remain nested; the pulse/stop control remains visible after inner approval; both tools finish once; one parent answer appears. |
| C-05 | AAAT outer decision: Deny, Cancel, and Stop Run in separate sessions. | The child never runs; the parent tool and root run end with the matching negative outcome. |
| C-06 | AAAT inner decision: Deny, Cancel, and Stop Run in separate sessions. | The child and parent invocation end cleanly; no parent tool remains Running and no speculative success is shown. |
| C-07 | Resolve outer and inner approvals from the Approvals page. Repeat from a second browser tab. | Chat updates without manual reload, attribution names the actor/channel, and the original run continues or terminates correctly. |
| C-08 | Reload chat while waiting for outer approval, waiting for inner approval, and while resumed work is running. | Canonical replay reconstructs the current state without duplicate messages, lost approval, or false idle state. |
| C-09 | Expand every tool card after completion. | Arguments, result/error, nesting, and status belong to the correct tool call; no empty or orphan tool appears. |
| C-10 | Select a different session during and after a run, then return. | Sessions do not leak messages, approvals, activity, or running state into one another. |
| C-11 | Send three ordinary prompts consecutively in one conversation, then reload it. | Every user prompt, assistant answer, and usage summary appears once in turn order both live and after canonical replay. |

## Agent configuration

Run this matrix when changing agents, models, providers, settings, ownership, or chat entry points.

| ID | Scenario | Expected result |
|---|---|---|
| A-01 | Click New Agent from chat. | A modal opens without leaving chat and uses the same fields and validation as Settings. |
| A-02 | Create and save a uniquely named agent, then start its first conversation. | The agent appears immediately, its chat connects without `409`/history errors, and the first run succeeds. |
| A-03 | Click Configure Agent, change one harmless field, save, close, and reopen. | The modal stays on chat, the change persists, and Configure Agent remains visible after cache refresh. |
| A-04 | Edit the same field through Settings and reopen the chat modal. | Both entry points display the same persisted value and validation behavior. |
| A-05 | Exercise an owner account and an admin account. | Create/edit/delete controls match Gateway ownership rules; unauthorized controls are absent and backend access still fails closed. |
| A-06 | Delete only the agent created by this test. | Referenced agents are blocked with a clear error; an unreferenced test agent is removed from Settings and chat. |

## Files and chat context

Run this matrix when changing chat attachments, conversation assets, agent workspaces, file references, document ingestion, or file authorization.

| ID | Scenario | Expected result |
|---|---|---|
| F-01 | Use the workspace attachment control to upload a small text file, send it with a prompt, then reload the conversation. | The upload is queued once, the agent can use the file as context, and the user message retains one attachment after canonical replay. |
| F-02 | Drag a supported file onto chat and send an attachment-only turn. | The composer accepts the drop, the empty text does not discard the user turn, and the agent receives the attachment once. |
| F-03 | Type `#`, open the agent file browser, select a workspace file, and ask the agent to use it. Repeat with a path containing spaces. | The composer receives the canonical `#path`, the authorized file is resolved without trailing prose, and no custom request field is required. |
| F-04 | In the file browser, create a folder; upload, replace, download, and delete a disposable file; then delete the empty folder. | Each operation updates the current listing, replacement requires confirmation, downloaded bytes match, and no unrelated file changes. |
| F-05 | Attempt a blocked file type, a file over 15 MB, and a workspace path traversal. | Each fails with a bounded actionable error; no staged file, partial file, or unauthorized path remains. |
| F-06 | Open files for an agent with sandboxing disabled, then exercise owner and non-owner access. | The disabled reason is visible; owner access follows policy and non-owner API access fails closed even if the UI route is forced. |
| F-07 | Attach a small image with a text prompt, send it, reload the conversation, then open Files for the same conversation. | The queued thumbnail, prompt, and sent image render without a broken-image state; canonical replay retains one prompt and one image; Files opens as a centered modal; Conversation attachments lists the image even when the sandbox workspace is disabled. |
| F-08 | Open Configure Agent from the workspace toolbar, then close it. | The editor is a centered modal over full-width chat, not a right-hand side panel. |

## Usage, context, and conversations

Run this matrix when changing token accounting, terminal Narad projection, compaction, or session/thread management.

| ID | Scenario | Expected result |
|---|---|---|
| U-01 | Complete a direct response, an ordinary tool run, and an AAAT run. | Each completed root run shows exactly one standard system text message with input, output, total tokens, and elapsed time immediately before the final run boundary. |
| U-02 | Reload each conversation from U-01 and switch away and back. | Usage summaries replay once with the same values and do not become assistant answers or custom events. |
| U-03 | Finish a run after an approval continuation, then deny and stop separate runs. | The final continuation has one usage summary; terminal negative runs do not remain active or create duplicate summaries. |
| U-04 | Rename a disposable conversation, reload, then archive it. | The session title persists and the archived conversation leaves the active list without deleting another conversation. |
| U-05 | Run a conversation until a context estimate exists, invoke Compact while idle, and send another prompt. | The context count refreshes, Compact is disabled during a run, compaction succeeds once, and subsequent canonical chat remains valid. |

## Dependencies and recovery

Run applicable scenarios when changing readiness, Manager, CredentialsHost, Podman, durable events, outbox delivery, or continuation. Stop only development processes created for the test.

| ID | Scenario | Expected result |
|---|---|---|
| R-01 | Stop Manager, then attempt to start a run. | The UI shows a stable actionable dependency error before execution; Gateway returns the correct failure. |
| R-02 | Stop CredentialsHost, then attempt a credential-dependent run. | The dependency is named correctly; no generic success, hang, or secret detail is exposed. |
| R-03 | Keep Manager running but stop Podman, then invoke container-backed work. | Manager returns a specific downstream error and Gateway/tool UI preserve it as a failed outcome. |
| R-04 | Restart Gateway during an ordinary main-agent run. | The durable run recovers or terminates explicitly; history remains valid and completion is not duplicated. |
| R-05 | Restart Gateway during an AAAT child run and during inner approval wait. | Parent/child lineage survives, approval remains actionable, and the parent completes at most once. |
| R-06 | Restart the browser or open a second tab during paused and running states. | Both tabs converge on canonical history; an out-of-band decision is reflected in the original tab. |
| R-07 | Restore each dependency after a failure. | The banner clears from a successful readiness response and a new run succeeds without restarting unrelated services. |

## Required evidence

Attach or record:

- branch, commit, build mode, browser, and test account role;
- the IDs of every scenario run, with Pass, Fail, Blocked, or Not Applicable;
- prompts and created test-record names;
- screenshots for important running, approval, expanded tool, negative, and final states;
- relevant Narad session export for lifecycle defects;
- browser-console and service-log findings;
- every defect discovered and the scenario rerun after its fix;
- all skipped scenarios and why they were not applicable.

Use this completion summary:

```text
Browser verification scope: Regression | Full
Branch/commit:
Build and stack:
Scenarios passed:
Scenarios failed:
Scenarios blocked/skipped:
Console/network findings:
Service-log findings:
Evidence:
Remaining risk:
```

Work is not done while an applicable mandatory scenario fails, a run remains ambiguously active, a tool failure is presented as success, or the reported scope overstates what was actually tested.
