# Single-instance channel concurrency

**Status:** Active
**Owner:** Kautuka channel runtime maintainers
**Activation condition:** One ChannelHost serves multiple independently configured Telegram bots.

## Scope

One ChannelHost process owns all Telegram bots. Bot polling uses asynchronous network waits; expensive work has separately bounded concurrency. Fifty distinct bots is the initial validation configuration, not a promised capacity limit or an agent count.

## Contracts

1. Each distinct Telegram bot has at most one active poll request. A supervisor reconciles additions, removals, credential changes and faults. A replacement never starts before its previous loop exits. Conflicting attachment ownership for one bot is rejected rather than resolved arbitrarily.
2. A bot can immediately begin its next poll independently of every other bot. Failures and provider backoff apply only to that bot. Cancellation and shutdown are awaited and observed.
3. Provider update identity includes the bot identity. Cursors are scoped to the bot and advance only after durable admission, or an explicitly ignored provider update. Credential rotation for the same bot retains its cursor; a different bot does not inherit another bot's cursor.
4. ChannelHost acknowledges callback receipt only after durable admission. Receipt never means that an approval has been authorized or executed. Gateway validates identity, ownership, expiry and the requested action.
5. Gateway acknowledges delivery only after durable acceptance or a recorded permanent rejection. Transient failures retain the event. An in-memory enqueue or duplicate cache alone is insufficient to transfer responsibility safely.
6. Short state changes preserve conversation ordering. Long agent runs never occupy the transport dispatcher or prevent callbacks for their own conversation from progressing.
7. Global and per-owner budgets bound queued bytes, event count, active processing and outbound work. Control operations have protected capacity. Queue saturation must not disable the operations required to drain or recover the queue.
8. Telegram send limits are scoped by bot and by bot/chat; platform safety ceilings are separate. Callback responses use their appropriate operation budget rather than ordinary message pacing.
9. Database transactions contain no network waits. SQLite lock wait, transaction duration, queue age, active pollers and callback API latency remain observable. Retention bounds diagnostic, idempotency and failed-work records.

## Delivery stages

- Stage 1: bot identity, durable event/cursor scoping, supervised independent polling and fault isolation. Preserve the existing Gateway handoff until its durable replacement is implemented.
- Stage 2: durable Gateway acceptance and failure classification, with recovery-safe idempotency and bounded fair dispatch.
- Stage 3: fast callback receipt acknowledgement, protected control capacity, concurrent outbound scheduling and correctly scoped limiter budgets.
- Stage 4: capacity admission, byte-based backpressure, storage/retention tuning and the full 50-bot workload validation.

Each stage must identify its completed guarantees and outstanding limitations. Stage 1 removes the demonstrated cross-bot polling gap; it does not establish the cause of historical single-bot delays or complete all scaling guarantees.

## Stage 1 status (October 16, 2026)

Implemented in one ChannelHost process. Independent supervised loops use Telegram-verified bot identities, reject conflicting verified ownership, retain bot-scoped durable cursors, isolate backoff and await cancellation before replacement. Invalid credentials cannot impersonate another bot through their token prefix.

Validation: the full solution builds with zero warnings and errors; all 143 ChannelHost tests and 181 channel integration tests pass. Eleven polling supervisor scenarios cover 50 outstanding polls, simultaneous durable admission, update identity/deduplication, cursor migration, token rotation, roster changes, ownership conflict, invalid identity claims, provider backoff and resource-limit isolation. These use fake transports; live callback timing requires restarting the dev stack and repeating the diagnostic capture. Stages 2–4 remain pending.

## Validation gates

### Durable session queue status (October 16, 2026)

Transport prompt admission now persists waiting work before acknowledging delivery. Session FIFO queues survive restart, use stable run identities for recovery, isolate concurrent sessions with an eight-worker ceiling, recheck permissions and expose scoped queue controls. A shared session execution gate and persisted lifecycle checks hold new channel turns while an existing run is executing or paused for approval. Approval continuations resolve their original prompt by run identity. Ingress orders short button decisions and prompt admission; actual approval execution uses the existing durable continuation dispatcher. Bot/chat admission is serialized across HTTP and transport entry points so main-chat/topic aliases cannot create competing first sessions.

This completes the session-queue portion of stage 2. Per-owner admission budgets, terminal metadata retention, complete control/outbound capacity isolation and the full 50-bot mixed workload remain outstanding. Existing overlapping runs require explicit stop/recovery; their histories are not rewritten.

- Fifty idle bots can all have outstanding requests; an active bot can repeatedly receive updates while the other 49 remain idle.
- Equal provider update ids on different bots produce distinct durable events. Repeated delivery on the same bot deduplicates.
- A failed or rate-limited bot does not delay a healthy bot. Immediate error responses cannot create a hot retry loop.
- Add/remove/credential rotation reconcile while polls are outstanding, with no overlapping requests for one bot.
- Duplicate bot ownership is visible and does not route traffic to an arbitrary user.
- Shutdown cancels and awaits all loops; restart retains admitted work and the appropriate bot cursor.
- Subsequent stages exercise simultaneous callbacks, one noisy owner, full queues, slow SQLite writes, restart at each handoff boundary and interrupted approval processing.
- Measure server receipt-to-acknowledgement-start separately from Telegram API latency and the user's observed spinner duration. Acceptance targets are evaluated under specified traffic and network conditions.
