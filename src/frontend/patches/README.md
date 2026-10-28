# Narad lifecycle replay compatibility

The pinned Narad alpha package is patched through pnpm's `patchedDependencies`.
The patch is applied by installation and covered by `naradHistoricalLifecycle.test.ts`.

Gateway previously persisted two invalid lifecycle patterns during nested approval continuation and restart recovery:

- Repeated `tool.call.started` for the same still-open call. Replay accepts it only when run, root, tool name, executor and arguments match, preserves the original tool state, and advances the journal cursor. Conflicting identity and starts after completion remain rejected.
- `run.error` with `gateway_process_lost` after an already terminal child. Replay preserves that child's terminal outcome and advances the cursor. Other duplicate terminal transitions remain rejected.

Gateway's writer and reconciliation service now prevent these patterns at their source. This compatibility patch preserves existing journal rows and queued prompts. Remove it when the pinned upstream package supports the same tested behaviour.
