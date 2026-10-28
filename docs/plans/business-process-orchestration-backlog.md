# Business-process orchestration backlog

**Status:** Active

**Owner:** Product architecture

**Activation condition:** A representative business process, its accountable owner, outcome contract, authority model, and external-system boundaries are selected for an end-to-end reference implementation.

## Product boundary

Kautuka is a business-grade agent orchestration platform for durable business processes. It is not a coding-agent harness and does not aim to reproduce Codex- or Grok-style developer teams, recursive subagents, worktrees, or pull-request automation.

Agent **workflows** ([agent-workflows.md](agent-workflows.md)) are a separate product: sequential composition of governed agents with typed state. A process activity may later invoke a workflow instance. Chat and Narad history are not process authority.

The platform owns process state, authority, policy, recovery, and audit. Agents are governed workers inside that process. Agent output is evidence or a proposed outcome; it does not advance or complete a business process until the configured validation, policy, and authorization gates accept it.

## Outcome

An organization can define, version, start, observe, govern, recover, and audit a long-running business process that combines deterministic activities, agent activities, human decisions, external events, schedules, and enterprise-system side effects.

A representative process must be able to:

1. receive a business request and assign a stable correlation identity;
2. collect structured inputs and supporting evidence;
3. invoke one or more governed agents or deterministic connectors;
4. branch, wait, run independent work in parallel, and join results;
5. require authorization according to business policy and separation of duties;
6. update external systems without duplicating effects after retry or restart;
7. escalate deadlines and route exceptions to accountable people;
8. survive process and host restarts without changing lineage or authority; and
9. produce a complete audit record of inputs, decisions, actions, evidence, and outcomes.

## Delivery principles

- Persist authoritative process state before publishing derived UI or notification state.
- Keep deterministic process control outside model context and prompts.
- Address every activity, wait, decision, external effect, and compensation by a durable identifier.
- Define typed input, output, evidence, error, and completion contracts for every activity type.
- Treat approval as an authorization decision, not proof that execution completed.
- Treat an agent's completion claim as untrusted until the configured outcome gate accepts it.
- Make retries idempotent and make non-idempotent effects explicit, reviewable, and compensatable.
- Preserve the existing canonical runtime-event, HITL, AAAT, credential, channel, and Manager authority boundaries.
- Introduce the smallest process model that proves the selected reference process; do not begin with full BPMN compatibility.

## P0 — process authority and execution contract

### BP-001: Process definition and immutable versions

Define a process-definition authority with immutable versions, lifecycle state, ownership, input/output schemas, activities, transitions, policies, deadlines, and compensation declarations. A running instance remains pinned to its starting definition version unless an explicit migration succeeds.

### BP-002: Durable process and case instances

Add a durable process-instance aggregate with a business correlation key, definition version, owner, participants, status, current activities, timestamps, and terminal outcome. Define whether case management is a separate aggregate or a process-instance profile only after validating the reference process.

### BP-003: Canonical process event model

Define append-only process-visible events for instance, activity, decision, wait, external-effect, escalation, compensation, and terminal transitions. Link agent runtime events to their owning process activity without making chat or Narad history the process authority.

### BP-004: Typed activity contract

Define the common activity envelope and terminal outcomes. Every activity declares typed inputs, outputs, evidence, timeout, retry policy, idempotency requirements, authorization policy, and permitted terminal states. Initial activity types are:

- agent invocation;
- deterministic connector or tool call;
- human task or approval;
- business-event wait;
- timer or deadline;
- validation or policy gate;
- notification; and
- compensation.

### BP-005: Agent activity adapter

Adapt the existing durable AAAT invocation aggregate to a process-owned agent activity. Preserve exact process, root run, parent activity, invocation, tool-call, approval, and continuation lineage. Support bounded parallel leaf invocations without making recursive agent teams the process model.

### BP-006: Idempotent external-effect ledger

Record the intent, business idempotency key, target system, request digest, attempt, acknowledgement, result, and reconciliation state for every external side effect. Retry only when the connector contract proves the retry is safe; otherwise route the activity to reconciliation or human exception handling.

### BP-007: Wait, resume, timeout, and restart semantics

Generalize the existing durable wait and continuation mechanics for business events, human tasks, timers, agent approvals, and external callbacks. Define first-valid-resolution, expiry, cancellation, late-arrival, duplicate-event, and restart behavior for each wait type.

## P0 — governance and accountable outcomes

### BP-008: Participants, roles, and delegated authority

Bind process initiators, owners, operators, approvers, service identities, agents, and external callers to explicit organization-scoped roles. Record the authority used for every privileged transition and external action without exposing reusable credentials to agents.

### BP-009: Approval policy and separation of duties

Extend approval policy from individual tool calls to business decisions. Support amount/risk thresholds, eligible approver groups, quorum, ordered approvals, separation of requester and approver, delegation, expiry, escalation, rejection, and reason requirements.

### BP-010: Outcome validation gates

Allow a process activity to require schema validation, deterministic rules, policy evaluation, evidence checks, independent agent review, human authorization, or a configured combination before its proposed output is accepted. Validation failure returns structured gaps or an exception outcome; it must not silently become success text.

### BP-011: Evidence and audit package

Retain the definition version, effective policies, structured inputs and outputs, approval decisions, agent and connector identities, external acknowledgements, referenced artifacts, validation evidence, exceptions, and final business outcome. Provide an exportable audit package with integrity and retention metadata.

## P1 — orchestration behavior

### BP-012: Branch, parallel split, join, and bounded fan-out

Add deterministic conditional transitions and parallel activity groups. Define join policies for all, any, quorum, first-success, and explicitly tolerated failure. Enforce bounded fan-out and cancellation propagation while retaining completed evidence.

### BP-013: Retry, compensation, and exception handling

Separate technical retry from business compensation. Model retry budgets, backoff, compensating activities, manual remediation, abandon/reject outcomes, and escalation when automatic recovery is unsafe or exhausted.

### BP-014: Queues, assignments, deadlines, and SLAs

Add durable work queues, role-based assignment, claim/release, priority, due dates, service-level timers, reminders, escalation, reassignment, and absence handling for human and agent work.

### BP-015: Business-event correlation

Accept authenticated events and callbacks from connectors and channels, correlate them to exact waits using stable business and platform identifiers, deduplicate delivery, reject unauthorized or stale events, and preserve unmatched-event diagnostics.

### BP-016: Process scheduling and reconciliation

Evolve Auto-Pilot scheduling into process-instance initiation and durable work reconciliation. For every runnable activity, guarantee that one eligible execution is active or a durable reason explains why it is not. Preserve overlap, lease, readiness, and notification semantics.

### BP-017: Human exception workbench

Provide an operator surface for blocked, timed-out, policy-rejected, reconciliation-required, and compensation-failed instances. Allow authorized retry, reassignment, evidence attachment, correction, compensation, cancellation, or terminal disposition without editing stored history.

## P1 — operations and lifecycle

### BP-018: Process fleet observability

Expose process and activity throughput, age, queue depth, SLA risk, failure categories, approval latency, agent and connector usage, retry/compensation rates, and stuck-instance detection. Preserve drill-down from business outcome to canonical process and runtime events.

### BP-019: Definition promotion and instance migration

Define draft, validation, approval, activation, retirement, and rollback rules for process-definition versions. Add a fail-closed migration mechanism for active instances, including dry-run validation, compatibility checks, audit evidence, and recovery from partial migration.

### BP-020: Retention, legal hold, and controlled deletion

Apply organization and process-class retention policy to events, decisions, artifacts, messages, evidence, and exported audit packages. Support legal hold and deletion workflows without silently breaking referential integrity or audit explanations.

## P2 — productization and ecosystem

### BP-021: Reusable process templates

Package reviewed process definitions, agent roles, connector requirements, approval policies, dashboards, and test scenarios as installable templates. Installation must not automatically grant credentials or business authority.

### BP-022: Connector conformance contract

Require connectors to declare authentication, authorization, idempotency, pagination, rate limits, callback verification, error taxonomy, retry safety, compensation capability, evidence handling, and data-classification behavior. Add conformance tests at the connector boundary.

### BP-023: Process simulation and policy testing

Provide deterministic definition validation and scenario simulation for success, rejection, timeout, duplicate delivery, agent failure, connector outage, restart, escalation, compensation, and unauthorized-action paths before activation.

### BP-024: Business outcome analytics

Separate business KPIs from agent telemetry. Allow process owners to define outcome measures, cycle-time dimensions, exception categories, cost attribution, and quality indicators without turning model-generated labels into authoritative business facts.

## Reference implementation gate

Do not declare the process platform ready from CRUD screens or a happy-path demo. The first reference process must prove:

- immutable definition/version binding;
- typed activity inputs and outcomes;
- deterministic and agent activities in one instance;
- parallel work and exact join behavior;
- a policy-controlled human decision;
- an idempotent external-system update;
- timeout and escalation;
- failure followed by safe retry or compensation;
- restart during an active wait and during external-effect reconciliation;
- complete process-to-runtime lineage; and
- an exportable audit package explaining the final business outcome.

## Explicit non-goals

- Reproducing a coding-agent TUI, Git worktree workflow, pull-request lifecycle, or code-review team.
- Making arbitrary recursive subagent spawning the primary orchestration abstraction.
- Storing authoritative process progress only in prompts, chat transcripts, model memory, or an in-memory coordinator.
- Treating a model's prose response, an approval decision, or message delivery as proof that a business outcome completed.
- Exposing framework-specific labels such as harness agent as user-facing process types.
- Implementing every BPMN construct before the reference business process establishes the required semantics.
