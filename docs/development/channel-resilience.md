# Channel resilience verification

The channel acceptance workspace has a deterministic resilience matrix. It exercises the same owned stack, standalone provider simulator, browser, and canonical runtime contracts as ordinary acceptance; it does not create a test-only runtime authority or a provider bypass.

## Profile invocation

The profile selects failure coverage only. The invoking CI or release policy must supply all execution quantities explicitly:

```text
KAUTUKA_RESILIENCE_PROFILE=pull-request|nightly|release-candidate
KAUTUKA_RESILIENCE_ITERATIONS=<positive integer>
KAUTUKA_RESILIENCE_CONCURRENCY=<positive integer>
KAUTUKA_RESILIENCE_MAX_DURATION_MS=<positive integer>
```

`pnpm acceptance:resilience` validates the profile/matrix contract without starting fixed-port services. The owned acceptance execution binds the matrix to concrete restart and partition drivers only when a policy supplies those values. The harness has no hidden default load, duration, or retry count.

## Implemented deterministic checkpoints

`pnpm acceptance:resilience:owned` (and `npm run test:resilience:owned` in `src/tests/channel-acceptance`) runs isolated owned-stack Playwright specs:

| Scenario | Boundary |
|---|---|
| `CRH-050-RESTART-MATRIX` | Gateway / ChannelHost / ProviderSimulator / browser reattach |
| `CRH-050-APPROVAL-PENDING-RESTART` | Gateway restart while a skill approval is pending |
| `CRH-050-CHANNELHOST-APPROVAL-PENDING-RESTART` | ChannelHost restart while a skill approval is pending |
| `CRH-050-SIMULATOR-APPROVAL-PENDING-RESTART` | ProviderSimulator restart while a skill approval is pending |
| `CRH-035-NESTED-GATEWAY-RESTART-AT-CHILD-PAUSE` | Gateway restart at nested child pause |
| `CRH-035-NESTED-CHANNELHOST-RESTART-AT-CHILD-PAUSE` | ChannelHost restart at nested child pause |
| `CRH-037-DEPENDENCY-OUTAGE-RECOVERY` | Manager pause → readiness deny → resume |

Evidence writers record scenario id, seed, configuration digest, process generations, and a minimal `replayCommand`. Process logs and `owned-processes.json` are retained under `.artifacts/channel-acceptance/<run-id>/` when `KAUTUKA_ACCEPTANCE_KEEP_WORKSPACE=true`.

Partition (`CRH-051`) and concurrency (`CRH-052`) matrices are contract-driven: they exercise the profile/failure mix and emit evidence without inventing product load thresholds. The approved light soak envelope (`CRH-053-LIGHT-SOAK`) records ~**200 canonical events/min** and **20 concurrent sessions** under absolute correctness only; multi-hour soak and percentile SLOs remain deferred. Failure triage requires seed + replay (`CRH-054`).

## Required matrix

| Boundary | Pull request | Nightly | Release candidate | Expected invariant |
|---|---|---|---|---|
| Gateway process | restart | restart | restart | Recovery starts from committed events/waits; no duplicate continuation or terminal effect |
| ChannelHost process | — | restart | restart | Ingress lease/cursor and delivery state recover without manual repair |
| Provider simulator process | — | restart | restart | Persisted simulator cursor, transcript, and fault state converge |
| Provider HTTP | Telegram transient failure | Slack rate limit | Telegram timeout and Slack rate limit | Bounded retry/backpressure and one durable outbound effect |
| Provider WebSocket | Slack Socket Mode disconnect | Slack Socket Mode reconnect | Slack Socket Mode recovery | Reconnect preserves event delivery and deduplicates ingress |
| Gateway-to-ChannelHost mTLS | — | partition | partition | Failure is denied/retried within the configured policy; no capability downgrade |
| Browser SSE | partition | partition | partition | Reattachment projects canonical truth, not a synthetic transcript |
| Service dependency | — | partition | partition | Readiness/error lifecycle is typed; deny/cancel/stop remain safe |

The pull-request profile is deliberately a small deterministic boundary check. Nightly and release-candidate profiles contain the full matrix. Provider faults are injected only through the authenticated standalone simulator control surface; process restarts are owned-child restarts only.

## Concurrency and soak invariants

Every profile must vary registrations, owners, conversations, sessions, approvals, attachments, and AAAT invocations at the supplied concurrency. It must assert owner/run isolation, ordered canonical sequences, first-valid resolution, exactly one parent terminal result, no unresolved active work after convergence, and no duplicate provider effect for a stable operation identity.

Each completed run records: profile, scenario, seed, configuration digest, canonical event range, simulator transcript, service findings, browser findings, process evidence, and a replay command. Failed runs additionally preserve the smallest failing transcript and canonical range. Retrying a flaky case without its seed/evidence is a failure, not a pass.

## Baselines and release policy still owned by the product owner

The code records observations (latency percentiles, queue depth, retries, unresolved work, storage growth, memory growth, and process/handle leaks) but does **not** declare product thresholds. Before this becomes a release gate, the owner must approve:

1. iterations, concurrent actors, and maximum duration for each profile;
2. latency percentile and queue/retry ceilings per provider and operation class;
3. maximum unresolved work, storage/memory growth, and process/handle leak allowance; and
4. the retry/flake policy and live-provider release-candidate eligibility.

Until those values are approved and measured from a baseline for multi-hour/release gates, a resilience run provides evidence but cannot claim a performance or soak SLO beyond the approved light envelope (200 events/min, 20 concurrent sessions).

## Reproduction

Evidence includes a minimal command such as:

```text
KAUTUKA_ACCEPTANCE_SEED=seed-42 pnpm acceptance:resilience:owned
```

Run it against an empty owned acceptance port allocation with the same profile quantities. Do not reuse a developer stack, mutate durable runtime rows, or copy provider credentials into evidence.
