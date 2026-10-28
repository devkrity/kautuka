# Signed product backlog

**Status:** Active — signed 21 Sep 2026 after Fable 5.1 re-validated an unbucketed list. Code items 1–4 landed the same day; remaining items are live proofs, land-on-main, and Narad extract ops.

**Owner:** Product architecture

**Activation condition:** Immediate. This is the signed unbucketed remainder after Fable 5.1; it does not wait on a later quarter.

This is the one list. Do not re-bucket by quarter. Neighboring items that were proposed and **cut** live at the bottom so they are not quietly reopened.

Related: [narad-one-protocol.md](narad-one-protocol.md), [agent-workflows.md](agent-workflows.md), [governed-workflow-platform.md](governed-workflow-platform.md).

## Done in tree (21 Sep 2026)

1. **Deleted `POST /api/workflows/{workflowId}/instances/{instanceId}/continue` and the dead frontend.** Contract test forbids the route. Owner UX is `resolve-interrupt` only. `ContinueAsync` stays as the internal command.
2. **`maxRetries` applies to `structured_output_invalid` for non-`external` steps.** Retry allocates `attempt+1` with a new `run_id` on the same node. Exhaustion is `step.budget_exceeded`. Never auto-retry `external`.
3. **Chat and Approvals `resolve-interrupt` go through `createCommandClient` from `@devkrity/narad`.** `stop-active-root` / `retry-target` / `cancel-graph-node` stay Kautuka-profile session commands.
4. **CI matches `feature/**` as well as `Feature/**`.** First green run (and therefore `NARAD_GIT_TOKEN` proof) still needs a push.

## Still to do

5. **Workflow template names are samples.** Payroll, Mortgage, Daily portfolio, and Family psychologist do not perform that business work. Catalog create was proven on `:20229`. Engine behavior is covered in-process: `workflow_review` approvals, resume from `maf_checkpoint_json`, switch first-match plus default, governed JSON fail-closed, and memory writes off unless a step sets `memoryWrites`. A live WebHost playthrough of the sample names is not the release gate.
6. **Land `feature/agip-product` on `main`.** `origin/main` does not have workflows or Narad product.
7. **Packed-card Stop on My Assistant.** Live 21 Sep 2026 on `session-example`: one `interrupt.resolved{decision: stop_run, members: 2}`, one `run.cancelled`, no `run.finished`.
8. **Expiry ghost.** Live 21 Sep 2026: card left Pending as Expired with no remount; store was `expired` / `system_timeout`. Abort-on-timeout had folded the journal as `stop_run`; the pack mapper now prefers expired so the journal decision is `expired`. Needs a rebuilt Gateway for a second live journal capture.
9. **Workspace snap on browser focus.** Re-proved live 21 Sep 2026: on `/workspace/My Assistant`, a focus/visibility refetch left the dropdown on My Assistant (did not snap to Roller).

## Narad extract (ops, not protocol design)

10. First CI green run is the proof that git-tag consume works off the author’s machine.
11. Reserve `@devkrity` on npm and the `devkrity` GitHub org before any publish. Kautuka stays on the private git tag until then.
12. `Devkrity.Narad.Contracts` NuGet only when a second host exists.

## Cut — already in tree or not this product

Already in tree: spec-shaped `resolve-interrupt`; PATCH approvals gone; ChannelHost → `IThreadCommandService`; `hitl.invalidation`; `Narad.Contracts`; packed nested HITL fold; `createCommandClient` in the Narad package; git-tag consume; packed `interruptId` join; expiry *code* path; workflow routes, `workflow_review`, `request_changes`, `AddSwitch`, `maf_checkpoint_json`, `memoryWrites` default false.

Cut as neighboring / not a demo line: `Narad.Server`, Python SDK, hash-chain journal, fan-out, sub-workflows, quorum, compensation, visual DAG, BPM, UpdateService `:20300`, `TryLoadDefinition` / tool-surface snapshot, `ForJsonSchema` probe, artifacts-by-hash copier (table may exist; do not build the copier for this ship).

Connector/web-fetch “journal delay”: misread. HTTP was 27ms; empty trace was HITL + gate-before-start. Outbox dispatch is inline; the 1s timer only retries failed leases. FICC bypass delay was fixed in `729a23f4`. Channel projector observer enqueues locally and skips `interrupt.*` — no inline network I/O on the card path.

## Settled — do not reopen without an experiment

Host run; `AIAgentHostExecutor`; manager LLM; AG-UI compatibility layer; “hashed journal” as a claim; fake Auto-Pilot session; holding finished step runs open.
