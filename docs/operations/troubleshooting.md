# Troubleshooting

## First checks

1. Confirm the expected process is running and the fixed port is listening.
2. Check `/health` and `/ready` on the relevant host where exposed.
3. Verify Podman availability for Manager/container failures.
4. Check the service state root selected by `KautukaHostPathPolicy`.
5. Correlate logs by trace/run/session/request identifiers without copying sensitive payloads.

## Startup failures

- Gateway rejects a non-empty incompatible SQLite schema. Reset the development state only when data loss is acceptable; production requires an explicit forward schema release.
- Missing internal-service certificates may keep a dependency unready even though Gateway starts because clients are lazy.
- Manager reloads accepted desired state; inspect its accepted/reconciled version and safe failure rather than only container presence.
- Cold MCP materialization can be long-running. Preserve cancellation/deadline diagnostics and inspect bounded stderr head/tail.

## Failed schema migration

Treat a failed migration as an integrity boundary, not a normal retry.

1. Stop only the service whose startup reports the migration failure; do not delete or edit its database, journal, or migration marker.
2. Preserve the release version, schema version, sanitized error code, database checksum/size, and the exact migration identifier. Do not copy rows containing credentials, prompts, or provider payloads.
3. Confirm whether the failure occurred before the migration transaction committed. If the state is uncertain, preserve the database and restore the complete protected backup into an isolated recovery environment.
4. Re-run the released migration/rollback procedure for that exact version. Never apply ad-hoc SQL or mark a migration complete by hand.
5. Before reopening traffic, run the relevant schema/contract suite and check that canonical root sequences, waits, invocation links, and ChannelHost delivery records are intact.
6. Escalate any partial or unclassifiable migration result with the preserved evidence; it requires an engineering recovery decision.

## Browser failures

- Verify `/api/auth/setup/status`, authenticated `/api/auth/me`, and `/api/narad/v1` discovery before debugging React state.
- Agent routes must reference an agent returned by runtime discovery after synchronization.
- Thread ownership/agent mismatch is a server validation problem, not something the browser should repair.
- After an out-of-band approval, look for a thread revision and canonical reconnect rather than an injected transcript message.

## Stuck approvals/runs

Inspect durable approval, wait, continuation, invocation, and runtime-event/outbox rows using their exact IDs. A resolved approval means ready to resume, not completed. Parent AAAT completion requires a terminal child outcome and one exact parent tool result.

Use this recovery order without editing authoritative rows:

1. Capture the correlation, root run, run, approval, wait generation, invocation, and parent tool-call IDs from sanitized logs and public diagnostics.
2. Confirm the approval has one committed decision and identify its actor/source. A second or conflicting resolution must not be applied.
3. Confirm the exact wait remains active until a continuation claim is durably acquired, and inspect lease owner/expiry rather than starting a manual continuation.
4. Confirm canonical event root sequence is contiguous and the outbox is either pending, leased, or delivered. Never manufacture a missing terminal event.
5. Restart the owning service normally to invoke durable recovery. Do not delete leases, waits, invocation links, or event rows as a repair.
6. For AAAT, confirm the child terminal event precedes the deterministic `agent.invocation.completed` event and that the parent tool call has at most one terminal result.
7. If lineage, sequence, or terminal uniqueness is invalid, preserve the database and evidence and stop; this is integrity failure, not an operator-repairable stuck run.

### Approval resolution dead letters

When `approval_resolution_events` reaches `dead_letter`, continuation remains blocked while `published_at IS NULL` or required stop/trust side effects are missing (`side_effects_applied_at IS NULL` when `stop_run` / `trust_this_run`). Approve-only publish dead letters still block until operator retry succeeds even though no side effects apply.

**List (AdminDiagnostics)**

```http
GET /api/runtime/approval-resolution-dead-letters?limit=50
```

Returns dead-letter rows with `eventId`, `requestId`, `attemptCount`, `lastError`, `stopRun`, `trustThisRun`, `sideEffectsAppliedAt`, and `publishedAt`.

**Retry (AdminDiagnostics, forward-only)**

```http
POST /api/runtime/approval-resolution-dead-letters/{eventId}/retry
```

- `202 Accepted` — row reset from `dead_letter` to `pending` (lease, error, and attempt state cleared); `side_effects_applied_at` and other already-applied markers are preserved; resolution dispatcher is woken synchronously.
- `409 Conflict` with `{ "status": "already_published" }` — canonical publish already completed; safe to ignore.
- `409 Conflict` with `{ "status": "already_pending" }` — row already pending/leased; wait for dispatcher or investigate lease owner.
- `404 Not Found` — unknown `eventId`.

This is forward-only recovery: it does not rewrite approval decisions, skip stop/trust side effects, or unblock continuation while resolution is incomplete. Unauthenticated callers receive `401`; authenticated non-admin callers receive `403`.

After retry, confirm `published_at` is set, continuation claim succeeds, and stop/trust side effects were not duplicated when only publish was needed.

## Narad cutover reset

Narad-only builds do not read browser-owned session history or caches created before Narad integration. When upgrading from a development database created before Narad, or after failed mixed-state experiments:

1. Stop Gateway and WebHost.
2. Delete development `kautuka.db`, per-agent `agent.db` files, and any leftover chat outbox/journal tables under the state root.
3. Clear browser site data for the WebHost origin.
4. Restart services and repeat first-run setup.

There is no compatibility flag or in-place journal migration path for browser chat.

## Channel delivery and provider outages

Runtime completion and provider delivery are separate. A completed run with an undelivered Telegram, Slack, or another channel message is a ChannelHost delivery problem and must not be resumed or re-executed.

1. Identify the attachment, owner, registration, conversation, idempotency key, operation state, lease generation, and provider mode without copying credentials or message bodies.
2. If the operation is `RESERVED`, allow its bounded lease/retry policy to recover it.
3. If it is `EFFECT_STARTED`, do not repeat the effect until the provider-specific reconciliation contract proves the outcome. Telegram uncertain effects remain operator-visible and are not automatically replayed.
4. If it is `COMPLETED`, return the cached result; never send again for the same scoped identity.
5. For a provider outage or rate limit, preserve durable work, honor provider retry guidance, and verify bounded backoff and queue growth. Do not move provider delivery facts into Gateway runtime state.
6. After recovery, validate cursor/session progression, provider transcript, normalized ingress deduplication, and outbound effect count before reopening the registration.

## Channel identity and key rotation

For mTLS or delegation failures, verify exact certificate CN, URI SAN, thumbprint slot, issuer, audience, method operation, request digest, token ID, and overlap window. Rotate through `next`, `current`, and `previous`; start ChannelHost before Gateway during activation. Never fall back to CA-only trust, unsigned context, plaintext HTTP/2, a general application secret, or caller-supplied capabilities.

For provider credentials, suspend the owner-scoped registration, rotate the secret in CredentialsHost, reprovision provider readiness, then reactivate. Do not persist plaintext credentials in attachment settings, simulator evidence, logs, traces, screenshots, or exception details.

### Certificate and signing-key rotation

1. Inventory the current, next, and previous certificate/signing-key slots, exact workload identities, audiences, activation/expiry times, and the approved overlap window. Keep private material outside logs and screenshots.
2. Generate and validate the next material using the released provisioning/rotation command. Confirm issuer, EKU, CN/URI SAN, pin, audience, method binding, and clock validity before it is trusted.
3. Install the next material alongside current material; do not replace current material in place. Start ChannelHost before Gateway so the receiving verifier recognizes the bounded overlap.
4. Exercise one authorized RPC and one negative RPC for current and next material. Replayed, wrong-audience, wrong-digest, expired, and unknown material must fail before a durable effect.
5. Promote next to current only after health and negative evidence pass. Retain previous only for the documented overlap, then revoke/remove it and prove rejection after the deadline.
6. Record only fingerprints, slots, timestamps, test outcome, and sanitized audit correlation. If the overlap is wrong or identity validation is ambiguous, suspend the affected registration and stop.

## Replay or delegation denial

1. Record the sanitized correlation, workload identity fingerprint, audience, method, request digest, token/nonce identifier, attachment/registration scope, and denial code. Never capture the signed token or credential.
2. Classify the denial: replay, expiry, wrong audience/method/digest, revoked key, owner/resource mismatch, or inactive registration.
3. Verify the caller is using the current operation-scoped delegation and exact resource binding. Do not retry by widening scope, disabling nonce checks, or substituting a general application secret.
4. For a legitimate stale/replayed request, discard it and let the owning durable worker obtain a fresh delegation. For a disputed denial, preserve the evidence and run the named negative contract test before changing configuration.
5. Confirm that no ingress acknowledgement, delivery effect, approval resolution, or continuation mutation occurred for the denied request. Any partial side effect is a security incident.

## Simulator or live-provider verification failures

Evidence must state `Official` or `Simulator`, sanitized endpoint identity, protocol version, configuration digest, scenario, seed, canonical event range, browser trace, provider transcript, and skipped cases. Simulator success does not override live-provider failure. An unavailable or unconfigured live suite is reported as not run, never passed. Do not import unreviewed live payloads into the simulator fixture corpus.

When a privileged channel control is questioned, start with the control-traceability table in `docs/architecture/security.md`, then use the named denial suite and its privacy-safe evidence. Do not infer runtime completion from provider receipts: the runtime-event catalogue is complete only for `GatewayRuntimeEventTypes`, while delivery evidence remains a separate ChannelHost record.

## Resilience failure reproduction

Do not retry a failed restart, partition, or soak case until its evidence bundle is preserved. Record the profile, scenario ID, seed, configuration digest, replay command, owned process list, smallest provider transcript, and contiguous canonical event range. Re-run only the recorded scenario on a new owned acceptance stack with the same explicit profile quantities; do not point the harness at a developer stack.

Classify the failure before retrying: illegal canonical transition, duplicate terminal result, unresolved wait, provider effect ambiguity, cursor/sequence loss, security-boundary denial failure, browser projection mismatch, dependency readiness failure, or resource observation outside the approved baseline. For a process/handle leak, preserve start/end process identity and measurement; do not change a threshold merely to make the run pass.

## Telemetry

All hosts use shared OpenTelemetry/logging wiring. Local deployments commonly export to OpenObserve when configured. Trace links are derived from persisted run correlation. Never enable payload logging to diagnose orchestration.

### OpenObserve startup and volume ownership

OpenObserve runs as container UID 0, which maps to the host user with Kautuka's default rootless Podman mapping. Its writable data bind mount does not request recursive ownership changes with `:U`. Other sidecars retain their ownership behavior, including the package proxy running as UID 1000. Read-only mounts remain read-only.

In a local Windows/WSL investigation on 2026-10-12, the data directory contained 39,710 files. An OpenObserve start using `:U` took approximately 249 seconds; queued Podman info requests completed when startup finished. Recreating the container with the same configuration except for removing `:U` took 2.8 seconds. Twenty concurrent info probes succeeded in 343–510 ms, health returned HTTP 200, and a diagnostic log ingestion succeeded. This strongly supports recursive ownership preparation as the cause of that delay; the isolated test did not include a full stack restart.

If the container user or user namespace mapping changes, verify data write permissions before relying on this policy. Existing data remains at the same bind mount path.

### Container inspection failures

Manager defers container reconciliation when Podman inspection times out, fails, or returns invalid data. A failed inspection does not establish that a container is absent or drifted. After a non-timeout inspection error, Manager checks `podman container exists`; only its explicit missing-container result permits creation. Failed removal also defers recreation. Container run commands do not use `--replace`, so a concurrent container creation cannot be overwritten implicitly.

Missing or stopped isolated connectors start on their next tool call. Manager startup audits existing running connectors without eagerly starting idle connectors.

### Repeated capability drift

Podman can expand `--cap-drop ALL` into individual capability names in `HostConfig.CapDrop`. Manager checks the resolved `EffectiveCaps` and `BoundingCaps` sets when supplied; both must be empty. Empty sets may serialize as `null` or `[]`. A nonempty set or added capability remains drift. Checking only for the literal `ALL` caused healthy agent containers to be recreated on each reconciliation pass.
