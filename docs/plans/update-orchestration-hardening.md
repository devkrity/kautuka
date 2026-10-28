# Update orchestration hardening backlog

**Status:** Active

**Owner:** Platform operations

**Activation condition:** Platform operations approves the durable update-operation aggregate and single-writer lease design before replacing the current Applying-state updater path.

## Objective

Make a Kautuka platform update a durable, observable convergence operation: after any retry, process restart, host restart, cancellation, or partial failure, the appliance must converge to either the complete target release or the complete last-known-good release. It must never report success for a mixed or unhealthy service set.

## Reference invariants

- Persist an immutable operation identity, target release, source release, and phase before performing side effects.
- Treat every service version pointer and data migration as a participant in one release transaction.
- Keep the previous complete release runnable until the target release passes verification.
- Make each phase idempotent and derive progress from durable on-disk facts, not the memory or version of the process currently running.
- Permit concurrent or repeated reconcilers; they may duplicate bounded work but must not corrupt artifacts or change the intended target.
- Declare success only after the complete release set is active, compatible, ready, and healthy.
- Automatically compensate to the previous release when activation or verification cannot complete safely.

## P0 - correctness and recovery

### UPD-001: Durable update-operation aggregate

Replace the coarse `Applying` state with an operation record containing a stable operation ID, immutable source and target release IDs, expected service/version manifest, phase, per-service progress, attempt, timestamps, and terminal outcome. Define explicit phases for discovery, download, verification, staging, quiescing, updater handoff, pointer switch, service restart, health verification, commit, and compensation.

### UPD-002: Single-writer lease with idempotent reconcilers

Add a durable host-scoped lease or generation token so the old UpdateService, staged UpdateService, startup worker, API request, and supervisor cannot independently drive conflicting operations. A new reconciler must adopt the existing operation rather than create another one. Stale owners must be fenced from later phases.

### UPD-003: Immutable artifact and version roots

Download to unique per-attempt temporary paths, verify before publication, and atomically rename into immutable release roots. Never delete or overwrite a target version root that may be used by another updater or active service. Detect and repair dangling pointers and incomplete roots without disturbing the current release.

### UPD-004: Release-set activation transaction

Prepare all service roots first, capture every prior pointer, and switch the declared release set using a transaction journal. If any switch fails, restore every pointer already changed. Remove the in-place-copy fallback from production layouts because it cannot provide atomic activation or trustworthy rollback.

### UPD-005: Explicit activation barrier and health gate

Do not finalize from Manager version equality alone. Require all expected services to report the target release ID, compatible protocol/schema versions, readiness, and a bounded health check. Persist `VerificationPending` while checking. Commit only after the complete barrier passes; otherwise enter compensation.

### UPD-006: Automatic compensation to last-known-good

On activation timeout, failed readiness, incompatible service versions, or restart exhaustion, restore the captured release-set pointers and any compatible data snapshot, restart in a safe order, and verify the previous release. Distinguish `RolledBack`, `RollbackFailed`, and `ManualInterventionRequired` terminal outcomes.

### UPD-007: Fail-closed release authenticity

Require an approved HTTPS source, signed release descriptor, artifact SHA-256 and size, and an expected platform/service manifest for production updates. Bind the verified descriptor digest to the durable operation so discovery changes cannot alter an in-flight update.

## P1 - operational safety

### UPD-008: Quiescence and drain protocol

Replace the point-in-time active-execution count check with a durable maintenance/drain state. Stop admitting new work, wait for active work within policy, preserve resumable waits, and record whether the update was drained, deferred, or force-activated by an authorized operator.

### UPD-009: Supervisor-owned restart protocol

Give one supervisor an explicit restart plan and acknowledgement contract. Avoid treating a no-block restart request, launcher timeout, or process termination as proof of activation. Record each requested and observed stop/start with the operation generation and release ID.

### UPD-010: Version-skew and data-compatibility policy

Publish supported rolling-version ranges, protocol versions, database migration direction, and downgrade compatibility in the release descriptor. Reject an unsafe update or rollback before switching pointers. Separate reversible configuration changes from irreversible data migrations.

### UPD-011: Retention and cleanup reconciler

Retain the current complete release, last-known-good release, and any release referenced by a live operation. Clean old artifacts only after an age threshold and only when no active pointer, operation, or concurrent temporary file references them.

### UPD-012: Update telemetry and operator evidence

Expose operation ID, phase age, lease owner, source and target releases, per-service versions, restart acknowledgements, health results, compensation state, and exact operator action required. Keep append-only phase history separate from the current snapshot.

## P1 - adversarial verification

### UPD-013: Phase-interruption test matrix

Add deterministic crash and cancellation injection before and after every durable write, artifact rename, pointer switch, restart request, health result, commit, and compensation step. After each interruption, start a fresh reconciler and prove that the appliance reaches one complete healthy release.

### UPD-014: Concurrent-updater convergence tests

Run old and new UpdateService instances plus repeated API and startup reconciliation concurrently against the same and different targets. Prove fencing, unique temporary paths, immutable version roots, non-dangling active pointers, and deterministic convergence to the authoritative operation target.

### UPD-015: Corruption and hostile-I/O tests

Cover truncated and oversized downloads, wrong hashes, invalid signatures, interrupted extraction, full disk, read-only paths, dangling links, locked executables, slow or hanging sources, malformed durable state, restart-command timeout, and partial service availability.

### UPD-016: Roll-forward and rollback compatibility matrix

Exercise upgrade, intentional channel rollback, failed upgrade followed by automatic rollback, rollback followed by a fixed upgrade, repeated rollback, and rollback blocked by an irreversible migration. Assert both filesystem state and actual service execution versions.

## Completion gate

Do not call update orchestration reliable until automated tests prove:

- cancellation or process death at every phase leaves the previous release runnable or recovery deterministic;
- repeated and concurrent reconcilers cannot corrupt artifacts or produce a mixed committed release;
- success requires the full service release set and health barrier;
- failed activation automatically restores and verifies last-known-good when rollback is compatible;
- production updates cannot proceed without authenticated metadata and verified artifacts; and
- an operator can explain every update and recovery transition from durable evidence.

## Reference lessons

- Grok Build is the strongest implementation reference for concurrency convergence, unique temporary files, atomic pointer swaps, rollback of multi-link swaps, cancellation testing, and authoritative-channel downgrade behavior.
- Codex's standalone installer is a useful reference for versioned immutable roots, verified release assets, an installation lock, and an atomic `current` pointer.
- Codex's package-manager update path is a warning: detecting the wrong installation method or updating a different binary than the one actually invoked can report success without changing the active installation.
