# Agent workflows

**Status:** Active. Unbucketed remaining work: [signed-backlog.md](signed-backlog.md).

**Owner:** Product architecture

**Activation condition:** Sequential v1 below is shipped on `feature/simple-workflow`. Competitive 90-day product (inbox, retries, one switch, fail-closed JSON, templates) is [governed-workflow-platform.md](governed-workflow-platform.md).

**Related Cursor plan:** `.cursor/plans/agent_workflows_plan_0d7554d8.plan.md` (mirror; this file is canonical in-repo)

**HITL seam collapse:** [narad-one-protocol.md](narad-one-protocol.md) — spec-shaped `resolve-interrupt`; channels as adapters; spec + `@devkrity/narad` + `Narad.Contracts` this quarter.

This feature is **not** the [business-process orchestration backlog](business-process-orchestration-backlog.md). A process activity may later invoke a workflow instance. Narad history is not process authority.

## Outcome

An organization can define, version, start, observe, govern, recover, and audit a sequential composition of existing Kautuka agents with typed state transfer, human review gates, crash-resume, and an exportable evidence trail.

## Product boundary

- **Agent:** one model, tools, skills, sandbox, approval policy. Still executed by MAF `ChatClientAgent` through Gateway chat execution.
- **Workflow:** first-class resource with its own storage locator. Deterministic graph of those agents. The graph, not a manager LLM, decides the next step.
- **Business process:** later layer. Do not rename this feature “business process.”

AAAT remains opportunistic in-step collaboration (leaf-only). It is not the workflow engine.

## Execution model

The **workflow instance** is the execution. It has run-equivalent lifecycle: `running | paused | finished | error | cancelled`, plus `stopRequested`.

Steps are workflow-owned **top-level agent runs** in fresh sessions. Intra-step HITL is ordinary outer HITL on that run. The step executor consumes the agent session journal until a root-run terminal (`run.finished` / `run.error` / `run.cancelled`) whose `runId` equals that attempt. `interrupt.requested` and `run.paused` are not terminals: nested HITL stays in-process in the same enumerator. If the chat stream ends at pause, the executor subscribes to the session hub first, then re-reads history (and reconnects on slow-subscriber overflow). There is no process-local waiter and no inferred `RunFinished`. Inter-step review is a workflow-scope interrupt plus `workflow.paused`. Stop is `workflow.stop.requested`, which must cascade `run.stop.requested` to every open owned run.

Do not invent a fake orchestrator *run* (no model, no sandbox, no tools). Parallel later is a cardinality change (`max_concurrent_steps`), not a new identity model.

MAF `WorkflowBuilder` walks the graph with **stateless** Kautuka executors that call `ChatGatewayService`. The instance row is the only checkpoint store. Never `AIAgentHostExecutor` (it bypasses journal, HITL, and sandbox).

For a run that finishes without a review pause, the final working data comes from the last executed terminal step. Output collection follows graph end steps, not steps with no incoming edges.

## Narad evolution

Narad is Kautuka’s protocol. Breaking changes are in scope.

Add `workflow.paused` / `workflow.resumed` / `workflow.stop.requested` / `workflow.step.started|finished|failed`. Interrupts gain `scope: run | workflow`. `run.started` gains `nodeId` and `stepAttempt` when workflow-owned. Flip `features.workflow` together with the reducer.

Keep: append-only journal; one allocator per ordering lane (workflow store only; mirrors never allocate); open-work invariant; projections are not authority.

## V1 requirements

Definition: immutable revisions; `input` / `working` / `output` schemas; linear nodes with prompt templates, restricted JSON Pointer maps (save-time resolvable), optional review gate, `sideEffects: none | idempotent | external`, `governed` flag.

Instance start snapshots definition-derived agent behaviour and tool-surface hashes. Memory facts and artifacts resolve at step start. Missing tools pause (`dependency.unavailable`); they must not silently disappear.

Structured output: one channel per run, chosen by capability probe (`ForJsonSchema` **or** `submit_step_result`). Workflow steps must not silently downgrade JSON to text. Nudges are tool-less.

### Governance that must ship in v1

If any of these are deferred, the feature is a demo with good state transfer:

1. Exactly-once step attempts: pre-allocated `run_id` committed before the agent starts; journal unique `run.started` per `runId`.
2. Workflow reviews live in the generalised approval store (first-valid, expiry, dead-letter, channel cards). Product UX is review, not “tool approval.”
3. Named failure codes. Reject at a gate is a decision, not a failure. Distinct: nudge, reject-rerun, retry, compensation (compensation is not v1).
4. `external` steps never auto-retry or nudge with tools.
5. Artifacts copied by hash into a workflow area. No shared mutable workspace across step agents.
6. Append-only hashed `envelope_revisions`. Resume is forward-only. No time-travel.
7. Step runs as the instance starter. Gate `separateFromStarter` checked before first-valid write. Intra-step tool approval policy unchanged.
8. Envelope size cap and secret-canary, fail-closed. Prompt injection through `working` is **not** solved in v1; bind blast radius with tool approvals.
9. Workflow clocks; overlap reject on `(definitionId, correlationKey)`; memory extraction off for step sessions unless opted in.
10. Instance, gates, and audit read the workflow store. Agent mirrors may lag.

## Surfaces

Workflows are a **first-class product area**, peer to Workspace, Approvals, and Auto-Pilot — not a tab inside agent chat and not Settings-only authoring.

- Routes: `/workflows` (definitions, start form, instance list) and `/workflows/:workflowId` / `/workflows/:workflowId/instances/:instanceId` (definition + live instance).
- Shell nav gets a Workflows item. Do **not** render workflow graph, envelope, or step progress on `AgentPageV2` / `/workspace`. Nested AAAT in chat stays hidden.

Workflow definitions reject back-jump cycles using the same ordered fall-through and explicit-target rules as runtime composition. A zero-rule default is an unlabeled continuation; joins remain ordinary single-execution steps reached by the selected path.
- The instance view watches the workflow instance session (`narad.workflow/v1`, graph hydrate `scope=workflow`). Step agent transcripts may be inspectable from that view; they are not the home of the feature.
- Approvals continues to list workflow-scope reviews next to tool approvals (same store). Opening a review deep-links to the workflow instance, not to `/workspace/:agentId`.
- Settings does not own this surface.

## Non-goals for v1

MCP idempotency ledger; compensation; quorum approvals; instance migration; crew-wide memory; LLM mapping; replaying a finished step; solving prompt injection; parallel nodes; visual DAG; `AgentWorkflowBuilder.BuildSequential` conversation chaining; **bundling this feature into `/workspace` or Settings**.

## Spikes (prove mechanism)

1. MAF `Executor<Envelope>` under Gateway AOT; checkpoint over the instance row; yield `ExternalRequest`; kill; restore; reach node 2.
2. Which providers honour `ForJsonSchema`; no-downgrade path fails loud on Relaxed.
3. Workflow-scope interrupt through wait-state / approvals / continuation, including process death. Spike is *how* to generalise the approval store, not *whether*.
4. Snapshot restore with a missing tool pauses; `TryLoadDefinition(locator, revision)` exists.
