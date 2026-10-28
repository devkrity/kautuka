# Engineering standards

## Core rules

- Make forward-only changes. Do not add legacy schemas, dual read/write paths, deprecated contracts, or compatibility shims without an explicit decision.
- Preserve Native AOT and trimming. Avoid runtime code generation, reflection-dependent persistence/serialization, and unrooted dynamic activation.
- Persist authoritative state before updating projections or returning success.
- Every externally observable command must be idempotent when retries are possible.
- Background work that matters after restart must have durable ownership, lease/retry semantics, and bounded shutdown.
- Use explicit source-generated JSON/protobuf contracts at process boundaries.
- Never log prompts, credentials, tokens, tool arguments/results, or command content in production.

## Backend

- Put endpoint composition in `Endpoints`, domain behavior in focused domain owners, and infrastructure behind narrow interfaces.
- Use explicit SQL columns and parameters. Transactions must match aggregate boundaries.
- Propagate caller cancellation unless the operation deliberately survives disconnect and durably owns its continuation.
- Keep host bootstraps thin and fail mandatory dependency activation clearly.
- Update architecture/contract tests when boundaries change.

## Frontend

- Validate API responses with Zod.
- Use TanStack Query for server state.
- Keep feature APIs, schemas, hooks, and components together.
- Keep `/api/narad/v1` as the browser chat runtime wire.
- Let the Kautuka workspace client reduce Narad authority into presentation state; do not add a second transcript, approval, ordering, or reconnect model.

## Files and complexity

Prefer focused owners over god files and forwarding facades. Split by semantic responsibility, not arbitrary line count. A new abstraction must remove duplicated authority or clarify a boundary; it should not hide control flow.

Partial declarations count as one conceptual owner. Constructor dependencies and public/internal method growth are architecture signals, not cosmetic metrics. Do not raise a ratchet, add a suppression, or create a forwarding partial to make growth pass. The only review path is the explicit [auditability decision rule](../architecture/auditability-decisions.md), agreed before implementation; there are no current channel/runtime exceptions.

## Tests

Durability changes require restart/idempotency/crash tests. Security changes require a negative test for the unsafe behavior. Wire changes require contract round trips. Browser lifecycle changes require browser-visible positive and negative paths.
