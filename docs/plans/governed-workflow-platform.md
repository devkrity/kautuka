# Governed workflow platform (SMB leader)

**Status:** Active — signed 20 Sep 2026 after five debate rounds. Unbucketed remaining work: [signed-backlog.md](signed-backlog.md).

**Owner:** Product architecture

**Activation condition:** Sequential v1 in [agent-workflows.md](agent-workflows.md) is the substrate. This plan is the competitive 90-day product that closes CrewAI/LangGraph gaps **without copying them**. Debate: platform architect, Fable opposing architect, and Grok VC for SMB/SME.

This is not the [business-process orchestration backlog](business-process-orchestration-backlog.md). Workflows compose governed agents with typed envelopes. Processes may later invoke workflows.

## Philosophy (must not break)

Durable, observable, governed, zero-trust. Gate-before-start, nested HITL, wait-state, resume. Append-only journal; one allocator per lane; projections are not authority. MAF `WorkflowBuilder` walks the graph; executors stay stateless and call `ChatGatewayService`. Instance row is the checkpoint. No manager LLM. No time-travel. No opaque pickle as evidence.

## Why CrewAI and LangGraph lose SMBs

| They demo | What a 20–200 person shop actually buys |
|---|---|
| CrewAI hierarchical manager / `allow_delegation` | Unattributable routing and an expanding tool surface |
| `memory=True` | Cross-client leakage (clinic, payroll, RIA) |
| LangGraph `interrupt()` as HITL | Pause in a notebook, not Approvals / phone |
| Opaque checkpoints / `thread_id` ownership | Restore lottery, no owner, no hashed evidence |
| Fan-out research crews | Pretty timelines; still need one human “send” |

Kautuka already has the substrate they lack: hashed envelope revisions, pre-allocated `run_id`, dedicated `workflow_narad_events` lane, `governed` save-time rules, overlap reject, recovery hosted service, agent sandbox/approvals on every step.

Sequential v1 still looks like a debugger at the **inter-step** gate (`POST …/continue`, unused `maxRetries`, linear-only graph). That is the hole this plan closes.

## Signed 90-day ship list (all three parties)

1. **One inbox.** Inter-step reviews are `kind=workflow_review` rows in the existing approval pipeline (expiry, first-valid, continuation jobs, channel cards). Approvals + Telegram are the owner UX. Verbs: `approve | request_changes | stop`. Not a fake MCP tool. Not `POST continue` as the product.
2. **Don’t lose the run.** SQLite checkpoint kill/resume; no duplicate `run.started` per `run_id`; overlap 409 on the start form; timeline shows paused / resumed / waiting-on.
3. **Safe retries.** `maxRetries` and `request_changes` allocate `attempt+1` with a new `run_id` on the **same node**. Never auto-retry `sideEffects: external`. Named codes: `gate.expired`, `gate.rejected_exhausted`, `step.budget_exceeded`. Envelope only moves forward.
4. **One switch.** MAF `AddSwitch` (1.21.0), first-match, required default, closed operators (`eq ne gt gte lt lte in exists`) over declared `workingSchema` fields. Save-time lint, including path-aware “every path to `external` crosses a review.” Journal `workflow.edge.taken`. No DAG editor. No independent `AddEdge` predicates (they can both fire).
5. **Fail-closed + least privilege.** Governed: JSON must parse as an object against the schema (no prose scrape). Ungoverned may extract-and-align but must journal `structured_output.extracted { droppedKeys }`. `memoryWrites` default off for `source=workflow`. Governed prompts render declared/mapped fields only — not the whole envelope.
6. **Ops UI + templates.** Start form, instance queue, live timeline, review cards. Design partner: **payroll**. Same primitives: mortgage (switch demo), daily portfolio advisor, family psychologist.

**Out this quarter:** fan-out/reducers, sub-workflows, quorum, org-level governed policy, multi-process instance lease, `ForJsonSchema` capability probe as a blocker, tool-surface snapshot program, artifacts-by-hash, compensation, visual DAG, manager LLM, time-travel.

## Graph primitives (MAF, not a homegrown engine)

Pinned package `Microsoft.Agents.AI.Workflows` 1.21.0 already exposes:

- `WorkflowBuilder.AddSwitch` / `SwitchBuilder.AddCase` / `WithDefault` (cases evaluated in add order)
- `AddEdge<T>(from, to, Func<T,bool>)` — **do not** use N of these for routing (accidental fan-out)
- `AddFanOutEdge` / `AddFanInBarrierEdge` — engine-ready, **not productised** until reducers + barrier restore are proven

Routing is host-evaluated over the envelope. Agents never set `nextNodeId`.

## Narad

Additive events only:

- `workflow.edge.taken` `{ from, to, caseIndex, pointer, op }` — authority for which arm ran
- `structured_output.extracted` `{ droppedKeys }` — ungoverned scrape visibility
- Keep `workflow.paused | resumed | stop.requested | step.*`

No new allocator. Journal remains `workflow_narad_events`.

## Approval generalisation

`tool_approval_requests` gains `kind` (`tool` | `workflow_review`) plus `workflow_id`, `instance_id`, `node_id`. Reviews reuse `approval_continuation_jobs`, `approval_resolution_events`, `HitlExpirySweeperService`, channel cards. `separateFromStarter` is enforced before first-valid write (bookkeeper starts, owner approves).

Resolution of `workflow_review` calls `IWorkflowExecutionService.ContinueAsync` with `approve | request_changes | stop`. Continue HTTP remains an internal command; instance UI links to Approvals.

## Four templates (dummy agent instructions, local Qwen)

| Template | Why it exists | Must work in UI |
|---|---|---|
| Payroll | Design partner | Correlation key, owner phone-approve before lodge (`external`), crash does not double-send, `request_changes` ≠ crash |
| Mortgage | Switch proof | proceed / refer / decline on envelope fields; decline is a finished business outcome |
| Daily portfolio advisor | Overnight durability | Morning brief parked; one approve to send; stop is obvious |
| Family psychologist | Memory + least privilege | No outbound without HITL; `memoryWrites: false`; governed prompts do not dump other-client fields |

Agents run on local Ollama `qwen-3.8` / `qwen3.8:latest`.

## Competitive position (what we say after this ships)

Kautuka is the workflow product you can **leave running overnight and approve from your phone**, with a hashed trail, without a manager LLM and without a Python notebook. CrewAI is a crew SDK. LangGraph is a graph library. Neither is a governed job runner for SMBs.

## Acceptance

Fable’s round-4 table in the debate transcript is the test list. Do not mark this plan done without: review row in Approvals (not a tool row), kill/resume from `maf_checkpoint_json`, switch first-match + default, governed fail-closed JSON, memory off on workflow sessions, and live UI of the four templates on WebHost `:20229`.
