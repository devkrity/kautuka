# Narad release gates

Performance and security are release gates for the Narad cutover. They use deterministic corpora, generous machine-independent budgets, and fail closed on material regression or unmitigated critical/high dependency findings.

## Commands

| Command | Scope |
|---|---|
| `pnpm narad:check` | Protocol schemas and golden traces |
| `pnpm --dir src/frontend verify` | Git-consumed Narad packages, web ratchets, production bundle |
| `node ./scripts/audit-dependencies.mjs` | Critical/high dependency audit |
| `dotnet test --solution src/Kautuka.slnx --filter "FullyQualifiedName~NaradSecurityTests|FullyQualifiedName~NaradPerformanceTests" -- --ignore-exit-code 8` | Gateway Narad security/performance suites |

CI runs these from [.github/workflows/dotnet-quality.yml](../../.github/workflows/dotnet-quality.yml).

## Performance corpora and budgets

| Gate | Corpus size | Budget |
|---|---|---|
| Client parse+validate+reduce steady state | 5,000 events | ≤ 4,000 ms median of 3 runs |
| Large authority replay | `authority-replay.jsonl` + 2,000 synthetic events | ≤ 6,000 ms median of 3 runs |
| Gap recovery backfill | 500-event catch-up after head | ≤ 2,000 ms median of 3 runs |
| Graph/progress replay | `run-graph.jsonl` + `progress-replay.jsonl` | ≤ 1,500 ms median of 3 runs |
| Dedup/history cap | 128-entry cap + 200 overflow inserts | Map size ≤ `maxHistoryEvents` |
| React notification fan-out | 120 progress updates | ≤ 120 selector rerenders; no-op gap clears do not rerender |
| Server history pagination | 600 events, page size 100 | 6 bounded pages, complete replay |
| Server projection rebuild | 500 events | ≤ 2 s |

Benchmarks warm up once, aggregate with median-of-three, and avoid process-wide heap assertions.

## Security gates

- Malformed/truncated/oversized SSE and JSON frames fail before reducer mutation.
- JSON Patch blocks prototype-pollution paths and unsupported operations.
- Unnegotiated profile types fail closed before mutation.
- Duplicate event IDs with conflicting payloads surface as resync/gap, not silent overwrite.
- Cross-session history/watch/stream/intent access returns forbidden for non-owner sessions.
- Client executor attach is gated when unsupported; oversize intents are rejected.
- Production bundles/source exclude legacy AG-UI/CopilotKit paths, `eval`, and dynamic wire imports.
- Protected identity fields are redacted from public Narad stream/history delivery (`NaradPublicAuthorityFilter` integration tests).
- Protected fields are redacted from public client errors (`releaseGates.security.test.ts`).

## Dependency audit policy

`scripts/audit-dependencies.mjs` audits the repo root and `src/frontend` workspaces at `--audit-level=high`.

- **Critical/high:** fail the release gate.
- **Compatible downgrades/pins:** pin direct dependencies to an audit-clean same-major release with a matching transitive pair. Current web pair: `react-router-dom@6.30.4` → `react-router@6.30.4` (latest audit-clean 6.x; React Router 7.x has no matched pair that clears high advisories until `react-router-dom@8.3.0` ships). Do not override transitive dependencies across major versions.
- **Audit reality:** when no same-major 7.x pair is audit-clean, downgrade the direct dependency to the latest clean major rather than cross-major overrides or silent suppressions.
- **Documented exception:** only with module, advisory id, expiry, and ADR owner recorded in the script's `DOCUMENTED_EXCEPTIONS` list.
- **Do not** silently ignore or suppress audit findings in CI.

When audit reports a fixable vulnerability, upgrade the dependency. When no fix exists, record a definitive blocker in the exception list with expiry and owner.

## Remaining risk

- Slow-consumer disconnect under extreme client backpressure is bounded in server design but not fully soak-tested here.
- Browser CSRF/origin behavior follows the current authenticated cookie guard; dedicated cross-origin mutation fuzzing remains channel-runtime scope.
- Live provider release gates remain in channel acceptance, separate from Narad package gates.
