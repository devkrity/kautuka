# Frontend architecture

The browser is a React/Vite administrative application. TanStack Query owns ordinary server state. The Kautuka workflow workspace is the sole production chat UX. Gateway remains authoritative for identity, ordering, persistence, approvals, and run state. **Narad** (`/api/narad/v1`) is the sole production wire protocol between Gateway and the browser.

## Application shell

Auto-Pilot schedule attachments are relative paths in an agent workspace. The schedule editor opens the workspace file picker for its target agent, or the first step's agent for a workflow target; uploads happen inside that picker. Workflow schedule string fields use the same agent's workspace for `#` file references. Scheduled runs execute on the server and cannot use files that exist only on the browser.

`src/frontend/web/src/app/AppRoutes.tsx` defines the route graph. Setup status resolves before guest/auth routing. Authenticated routes include dashboard, **workspace** (`/workspace`, chat only), **workflows** (`/workflows`, first-class workflow surface), approvals, connectors, Skills, memories, Auto-Pilot, settings, and onboarding. A workflow definition response includes a Mermaid diagram of the graph a run walks: each step, a pause when that step requires review, and a labeled arrow for each saved choice. The definition page draws that string. It is a picture of the saved definition. The run page draws that same picture and marks the arrow recorded by `workflow.edge.taken`. Closing a pause publishes one `workflow.resumed` for the wait that closed. The next step is added under the list. Between steps, the run continues, or a condition box sends it to a step when a field matches and to otherwise for every other value. Saving writes that condition onto the definition the run already evaluates. Settings and onboarding require an admin profile. Workflows are not rendered inside `AgentPageV2`.

The cross-system name, wordmark, mark, gradient, and icon rules are defined in the [Brand system](branding.md). The web app uses the canonical vermilion–turmeric gradient lockup and mark from `src/frontend/web/public/assets/`; the Windows installer and desktop tray use matching artwork. The gradient is reserved for the wordmark and large branded headlines or taglines, not body copy.

Selected external payloads are validated with Zod; other feature APIs currently rely on typed Axios contracts. Authenticated fetch owns cookies/session refresh. Feature folders own their API queries, schemas, hooks, and components; pages compose them.

## Client packages

Production chat uses two thin packages under `src/frontend/packages/`:

| Package | Role |
|---|---|
| `@devkrity/narad` | Headless reducer, replay, session attach, lease, and targeted client execution |
| `@devkrity/narad-react` | Generic React provider, store hook, snapshot hook, and selector hook |

Kautuka-specific selectors, transport wiring, approval presentation, and execution graph UI live in `src/frontend/web/src/features/chat/kautuka/`. They are **not** exported from `@devkrity/narad-react`.

Dev-time resolution maps package names to source entrypoints via `src/frontend/scripts/package-source-aliases.mjs`. Production bundles import emitted `dist` artifacts only.

## Production workspace (`/workspace`)

`AgentPageV2` is the Kautuka conversation workspace. It is a **dumb renderer** over Narad authority:

- `createKautukaNaradTransport` attaches to `/api/narad/v1` session watch streams with authenticated fetch.
- `@devkrity/narad` reduces committed authority events into deterministic presentation state.
- `@devkrity/narad-react` exposes that state through `NaradProvider`, `useNaradSnapshot`, and `useNaradSelector`.
- Kautuka hooks and components in `web/src/features/chat/kautuka/` map Narad facts to product UI (approvals, execution graph, composer, session rail).
- A connector may return a view with a tool result when the user should see the document itself. Gateway journals it as `view.opened` on the `narad.ui/v1` profile. The workspace draws a record, file chips, and actions. An action sends an ordinary user message. It does not emit `view.action`. Microsoft 365 mail and Google mail open this view for a message and for an attachment. The opened message lists its attachments for the agent. When the agent asks for one file, Gateway reads that saved file through the same document ingestion used for an uploaded file. Text and documents come back in the tool text in full. An image is sent as image content, the same way an uploaded photo is. List, draft, and sent results stay in the tool text.
- Transcript turns merge reasoning across assistant delivery-attempt `messageId`s in the same user turn so HITL resume does not hide pre-approval thinking when final text arrives on a later message id.
- Packed HITL ledger rows come from session REST approvals (true per-tool Approved/Denied/Pending). The single Narad packed interrupt is suppressed when REST already has that request id.
- Native `TextReasoningContent` is `reasoning.*` only. A provider echo of the same thinking as `TextContent` (tokens joined by newlines) is dropped so it does not fill the assistant body or become a channel “final.”
- Session revision invalidation uses `/api/narad/v1/sessions/{sessionId}/watch` and attach when no Narad delivery is actively streaming.

Gateway's canonical journal owns root life. Chat is a peer projector for continuity (same class as ChannelHost and Approvals). Root **attach** is the open-chat heartbeat; local run/resume is only the local turn or local approve envelope. See [ADR 0005 chat continuity](../adr/0005-channel-runtime-projection-and-verification.md).

The Kautuka web layer owns:

- authenticated request headers;
- selected agent and explicit thread route;
- session rail and creation;
- durable revision invalidation → attach when no Narad delivery is actively streaming;
- **atomic thread commands** (`resolve-interrupt`, `resolve-all-interrupts`, `stop-active-root`) issued through `kautukaNaradIntents.ts` — no browser-side bypass/stop/deny sequencing;
- Files explorer and Configure Agent as centered modals (`AgentFilesPanel` and `AgentConfigurationDialog`); they do not split the chat layout into a side panel;
- user-file composer (picker, drop, paste) that uploads conversation assets over REST, then sends `attachmentIds` on the Narad user intent;
- product-specific approval action/activity presentation and execution graph UI.

Forbidden: skipping attach because this browser "started" the root. Allowed: defer competing attach while an Narad delivery is actively streaming.

The frontend does not reconstruct transcript order, approval state, or nested execution from raw runtime records.

Conversation file bytes do not travel in Narad Protocol events. Workspace uploads and lists them with `POST`/`GET /api/sessions/{sessionId}/assets` and downloads with `GET /api/sessions/{sessionId}/assets/{assetId}`. Transcript chips and hydrate use `attachmentIds` on `message.user.recorded` and `messages.snapshot` only; Kautuka does not emit Narad `attachment.*` lifecycle events.

Agent create/edit catalog still lives on Settings. Workspace **Configure Agent** opens the same `AgentConfigurationDialog` modal used for that editor (its own `useAgentsTab` draft). **New Agent** continues to route to Settings.

## Out-of-band changes

The thread change feed carries `session.revision` invalidation. Resolution may emit an immediate revision, with later pause/terminal revisions as execution advances. An accepted revision replaces the thread-scoped Narad projection and reconnects to canonical history. It is not chat content and is not merged into a second client model.

## Stop behavior

The browser sends a single durable **`stop-active-root`** thread command to Gateway first. Only after it succeeds does chat abort the local delivery surface. Closing a browser stream is not a run stop.

## Thread command intents

The workspace issues one typed backend command per user action. `kautukaNaradIntents.ts` is a thin HTTP wrapper only — no orchestration helpers.

| Intent | Endpoint | Notes |
|---|---|---|
| `resolveInterrupt` | `POST /api/narad/v1/.../commands/resolve-interrupt` | Approve/deny with optional `trustThisRun` / `stopRun` in one durable command |
| `resolveAllInterrupts` | `POST .../commands/resolve-all-interrupts` | Resolve all pending waits for the thread/run |
| `stopActiveRoot` | `POST .../commands/stop-active-root` | Durable stop plus deny pending waits for the root |
| `resumeWaitingRoot` | `POST .../commands/resume-waiting-root` | Publishes `run.resumed` when root is paused; attach remains primary hydration path |
| `retryTarget` / `cancelGraphNode` | `POST .../commands/retry-target` / `cancel-graph-node` | Enabled only when graph node exposes retry/cancel targets |

Gateway resolves approvals with `HitlResolutionSource.ThreadCommand`, enqueues durable continuation, and publishes canonical events. The browser reacts through workspace projection and revision attach — it does not reconstruct external resolution from progress records or build resume arrays locally.

Approvals pane, channel callbacks, and thread commands converge on the same durable root-scoped semantics via `POST /api/narad/v1/commands/resolve-interrupt` (`interruptId` + `decision`) with optional `trustThisRun` / `stopRun` (no separate bypass-then-resolve sequence and no process-local stop registry authority). `PATCH /api/approvals/{id}/resolve` does not exist.

## Onboarding

First-run setup status fetches provider inventory and runtime health, but the empty-system redirect is based on `providersCount === 0`. An authenticated admin with no providers is redirected to onboarding, except while already on onboarding or settings.

The wizard is sequential:

1. validate provider;
2. save provider and a credential when supplied or required;
3. optionally discover model names and probe the selected candidate;
4. optionally require confirmation for a warning;
5. save model;
6. save agent;
7. refresh setup status.

This is not a cross-resource transaction. A provider can be persisted before a model warning pauses the workflow. Manual configuration routes directly to the Providers settings tab.

## Frontend gates

Run `pnpm verify` from `src/frontend`. It consumes `@devkrity/narad` `0.1.0` from npm, runs export smoke tests, and executes production ratchets. Architecture tests assert that chat uses the canonical Narad path and does not grow duplicate hydration, diagnostics, or projection owners. Production bundle checks fail if CopilotKit, assistant-ui, or retired chat-protocol packages appear in manifests, source, or built app chunks.

## Destructive upgrade

Narad is forward-only. There is no compatibility flag and no client-side migration shim. After upgrading to an Narad-only build, reset development databases and browser storage:

1. Stop Gateway and WebHost.
2. Delete the development `kautuka.db`, per-agent `agent.db` files, and any leftover chat journal tables if present under the state root.
3. Clear site data for the WebHost origin (localStorage, IndexedDB, service workers).
4. Restart the stack and complete first-run setup again.

Production upgrades require an explicit schema release and operator-run reset procedure; do not attempt in-place replay of browser-owned session state from before Narad integration.
