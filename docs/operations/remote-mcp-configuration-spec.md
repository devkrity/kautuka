# Remote MCP configuration and setup simplification

Status: implemented and locally reviewed, with verification limits recorded below. Recorded 2026-11-05 before implementation on `enhancement/remote-mcp-configuration`; final local audit 2026-11-06 (Australia/Brisbane). No commit, push or deployment performed.

This document records the agreed requirements and final review amendments. Use its numbered requirements as the implementation compliance checklist. Record evidence and remaining gaps at the end; do not weaken requirements to match implementation.

## Scope and architecture

R1. Remote HTTP MCP connectors only. Gateway connects directly to the remote endpoint. No managed container, proxy service, or relocation of MCP execution into CredentialsHost is involved. Preserve other connectors, OAuth, container execution, user data and unrelated runtime state.

R2. Forward-only remote configuration contract: no legacy migration or compatibility layer is required. Replace remote `envKeys` authoring and implicit `MCP_AUTH_TOKEN` bearer generation with structured credential declarations and explicit `httpHeaders`. Shared types must preserve container connector behavior. No vault deletion or runtime reset is authorized.

R3. CredentialsHost securely stores and resolves credentials and communicates only with Gateway. Gateway owns remote MCP transport, permissions, destination policy and request-time injection. The model supplies ordinary tool arguments, never destination overrides, credential references or authentication headers.

R4. Credentials remain stored in CredentialsHost. Trusted Gateway request execution resolves necessary references immediately before transmission and places values only into intended outbound headers. Resolved values must not enter agent-visible data, persisted connector configuration, shared client defaults, discovery caches, logs or diagnostics. Provider output receives best-effort screening for known echoes. A malicious credential recipient can transform a secret in ways screening cannot comprehensively detect. Do not claim guaranteed immediate erasure of managed strings or networking buffers.

## Setup and configuration

R5. Provide a direct **Add remote MCP** action: name, server URL, authentication (None/Bearer token/API key header), masked credential input, Required/Optional choice, expandable additional headers. Generate the ID and editable name suggestion from the parsed hostname only; exclude userinfo, path, query and fragment. Advanced holds description, icon and protocol overrides. No JSON, environment variable names, commands or container fields are required for ordinary remote setup. Keep other connector creation choices and JSON import.

R6. One model: authentication controls compile to `httpHeaders` bindings, never a separate competing auth representation. Each credential definition has a stable connector-local `key`, user-facing `label`, and explicit `required` boolean. Each header has `name` and exactly one of `value` (nonsecret literal) or `credentialKey` plus optional literal `prefix`. References resolve within authorized connector/caller scope; generic keys cannot collide across connectors.

```json
{
  "id": "tavily",
  "name": "Tavily",
  "transport": "http",
  "httpUrl": "https://mcp.tavily.com/mcp/",
  "credentials": [{ "key": "api-key", "label": "API key", "required": false }],
  "httpHeaders": [
    { "name": "X-Tavily-Access-Mode", "value": "keyless" },
    { "name": "Authorization", "credentialKey": "api-key", "prefix": "Bearer " }
  ]
}
```

R7. Reject case-insensitive duplicate header names, invalid names, CR/LF and invalid control characters, mixed literal/reference sources, undeclared references, and transport-controlled headers (Host, framing/connection and MCP session/protocol headers). Reject `Proxy-Authorization`; proxy authentication is transport configuration. Sensitive header values use vault references. No general expression evaluator or conditional/alternative header group engine.

R8. Enforce in Gateway: maximum 20 connector headers, 256 bytes per name, 8 KiB per final value including resolved credential and prefix, and 32 KiB aggregate connector-defined names and final values. Transport-generated headers use transport controls. Validate after resolution inside private request execution. Errors identify header name/index and rule/limit, never value, prefix or secret fragment.

R9. Missing required credentials block before network transmission. Authoritatively absent optional credentials omit dependent headers and continue. Whitespace-only values count as absent. Never send prefix-only/empty auth headers or unresolved placeholders. Vault outage, denied access and decryption failure fail the operation; they are not absence. Rejected supplied credentials fail visibly; no automatic anonymous retry.

R10. Credential editing distinguishes unchanged, replace and remove explicitly. Blank input preserves saved credentials. UI states include Not configured, Configured, Replace and Remove. Preserve personal/system ownership rules and exclusion of system credentials for personal endpoint overrides. Removal/status accurately account for any permitted remaining scope.

R11. Separate configuration status from last connection verification. Generic optional absence is "Optional credential not configured"; search presets may say "Using free access" and "Free access · Usage limits apply". Saved credentials do not prove validity. Other states include no authentication, using configured credentials, required credential missing and connection failure.

## Transport and destination policy

R12. A shared Gateway HTTP handler/factory serves testing, initialize, discovery, notifications, invocation, stream establishment, reconnect and session termination. Bind it to trusted immutable caller/connector/configuration context. Validate each actual destination, resolve only needed refs through CredentialsHost immediately before the inner send, and inject on the individual request. Do not pre-resolve remote credentials into environment dictionaries, MCP AdditionalHeaders, HttpClient default headers or cached clients/configuration. Prevent header/body capture; keep retry/logging layers from recording secret-bearing requests and clean injected headers after their use.

R13. Disable automatic redirects for all remote MCP requests. Report sanitized redirect errors. Independently validate any provider-advertised endpoint before use; a provider cannot grant a new credential destination. Preserve existing internal-address/admin and personal-endpoint policies rather than inventing a blanket ban on internal MCP servers.

R14. Every request remains bound to an approved destination/policy context. Every new socket uses the same resolved address that passed policy validation, preventing DNS time-of-check/time-of-use gaps. Preserve authorized hostname in HTTP authority/Host, TLS SNI and certificate identity validation when connecting to a validated IP. Never weaken TLS validation. Cover private, link-local and metadata addresses under existing policy.

R15. Bind reuse to destination, effective permission scope and configuration revision. Changes to endpoint, header bindings, effective credential presence/value revision/scope or permissions make affected connections, open streams, sessions and discovery ineligible for reuse. Invalidate only affected contexts. Use opaque revision metadata, never values/value-derived hashes as cache keys. Initialize replacement sessions before intended work. Never automatically replay a tool call with uncertain completion.

R16. Screen provider metadata, schemas/descriptions, results, notifications and errors for known credential echoes before agent exposure, with bounded short-lived private screening material. Screening must not introduce plaintext session caches. Sanitize local exceptions and remote error bodies; disable secret-bearing telemetry. Configuration reads/exports return refs and presence only.

## Testing drafts, persistence and import

R17. Test performs initialize/list, not arbitrary tool invocation, and uses the same policy/injection path as runtime. New secrets go through credential administration/storage first; test payloads contain temporary refs, not plaintext secrets. Temporary sets are caller-bound and bound to the candidate connector/destination. TTL is 15 minutes, maximum five outstanding sets per user. Enforce expiry server-side without browser interaction; cleanup on cancellation/abandonment where detectable.

R18. On successful save, promote/transfer temporary credentials to durable storage atomically where feasible or idempotently under retry. Durable association is source of truth; temporary deletion is later cleanup. Partial failure retry creates one durable association. Promotion failure must not report complete setup success; keep temporary values until retry/expiry/removal. Keep form data on failures and close only after requested configuration/credential work succeeds.

R19. Saving may succeed while provider is unavailable; connection status is separate. Draft tests cannot update saved discovery until draft configuration is saved. Connection field changes invalidate draft test results. Editing/toggling preserves all supported configuration. Agent access still requires existing connector/tool permission controls; discovery is not permission.

R20. JSON import supports common remote `url` and `headers`, presents endpoint/header sources/requirements for review, and routes sensitive values into CredentialsHost with refs before connector persistence. Mask secrets in preview. Known Authorization, Cookie and API-key headers default to sensitive handling; user can mark others. Reject Proxy-Authorization, URL userinfo and recognized credential-bearing query parameters with remediation guidance. No guessed query-to-header mappings. Token patterns only assist review, never prove nonsecret safety. Unsupported fields/placeholders produce visible feedback rather than silent dropping. Imported/manual connectors use the same editor/model.

R21. Audit connector create/update/enable/disable, credential add/replace/remove and connection tests with actor, connector, scope, operation/outcome and ref where appropriate; no values/resolved headers. Retain existing probe concurrency/abuse controls through shared path.

## Presets

R22. Prefill ordinary remote connector definitions, names/descriptions, optional API key declarations and bindings:

| Preset | Endpoint | Free headers | Optional API-key header |
| --- | --- | --- | --- |
| Tavily | https://mcp.tavily.com/mcp/ | X-Tavily-Access-Mode: keyless | Authorization: Bearer credential |
| Exa | https://mcp.exa.ai/mcp | None | x-api-key: credential |
| Firecrawl | https://mcp.firecrawl.dev/v2/mcp | None | Authorization: Bearer credential |

Tavily defines precedence when both keyless and bearer headers are present; Kautuka sends both when a key exists and implements no conditional suppression or rejected-key fallback. Verify the provider contract before acceptance. Exa keyless mode is documented; the pre-implementation live probe received Cloudflare 403. Tavily/Firecrawl anonymous initialize, discovery and search succeeded before implementation; this is not runtime implementation validation.

Make presets available on fresh/existing installations while preserving edits, credentials, enablement and deliberate removal; do not recreate deleted presets every startup or grant agent access automatically.

Provider references: [Tavily keyless](https://docs.tavily.com/documentation/keyless), [Exa MCP](https://exa.ai/docs/get-started/exa-mcp), [Firecrawl MCP](https://github.com/firecrawl/firecrawl-mcp-server).

## Blocking acceptance and regression coverage

R23. Extensive automated coverage must establish:

- Simple URL setup, auth/header forms, round trips, edits/toggles/import preservation, explicit mutations, statuses, save failures and draft-test invalidation.
- Header validation and limits after resolution with secret-free errors; declaration/scope isolation and required/optional/failure semantics.
- Credentials resolved only in final Gateway handler, absent from catalogues/agent outputs/configuration/logs/caches; same behavior for test/discovery/execution; best-effort echo screening.
- Validated address equals connected address with hostname authority/SNI/certificate validation.
- Policy changes prevent pooled/session reuse; no replay after uncertain invocation.
- Redirect rejection across initialize/discovery/invocation/stream establishment; advertised endpoint policy.
- Personal endpoint overrides, credential scope and permitted/prohibited internal destinations.
- Server-authoritative temporary expiry, quota, cancellation and idempotent promotion including cleanup failure.
- Audit events omit secrets and existing probe controls remain effective.
- Other remote connector behavior, container connectors, mail/OAuth and shared credential consumers retain their supported behavior.

R24. Live verification distinguishes discovery from actual search. Exercise anonymous search through implemented Kautuka paths for Tavily and Firecrawl; record Exa success or concrete remaining provider restriction. Authenticated behavior requires securely supplied test keys; never invent credentials or expose them. If unavailable, report authenticated live verification as pending while testing deterministic local equivalents extensively. Do not mark unsupported live claims as verified.

## Implementation chunks and compliance evidence

Implement bounded reviewable chunks: (1) schema/contracts/validation/persistence, (2) late transport injection and network/session policy, (3) credential draft lifecycle/import/UI, (4) presets and integrated regression/acceptance. Adjust chunk boundaries when owning-code dependencies justify it. Review each before delegating the next. No commit/push/publication is authorized by this document.

### Implementation review clarifications

- Review against `main` at `5ff280a` confirmed the current ConnectCallback already validates and connects to the same resolved IP while retaining the URI hostname. R14 requires regression proof and preservation, not replacement of that mechanism.
- Remote probe/runtime handlers disable implicit OS/environment HTTP proxies: otherwise ConnectCallback can validate/connect to a proxy socket while that proxy independently resolves the MCP destination, breaking the approved-destination address binding. Keep the hostname URI and existing direct DNS/socket validation; do not introduce a proxy tunnel implementation in this scope.
- Remote handlers disable automatic cookie storage. Otherwise provider `Set-Cookie` processing occurs inside the HTTP stack before the outer response screening handler, allowing a raw credential echo to become session-cookie state and be sent as an unintended header. Explicit owner-configured credential-backed `Cookie` headers remain supported; MCP session headers remain owned by the SDK.
- Runtime MCP invocation already creates/disposes clients per call. Preserve that lifecycle; do not introduce a pool to satisfy R15. Fix actual catalogue invalidation for declarations, key presence/revision, caller permissions and personal URL, and keep each live operation bound to current policy.
- Credential-only association switches preserve shared public configuration and its `UpdatedAt`. They use the existing scoped opaque user/system revisions to cancel and invalidate affected contexts. A new snapshot's global association ID must not by itself invalidate other users whose effective credentials/destination did not change. Restart epochs invalidate persisted catalogues without storing credential values or their hashes.
- Gateway's shared HTTP handler is the injection seam. A per-call client does not justify resolving credentials before request execution; initialization, SSE and tool sends must retain the final-send boundary and avoid fixed secret headers.
- Exa documents direct IPv4 as a condition for keyless access; IPv6/proxied paths may require a key or OAuth. Show actionable guidance and verify actual path before attributing an observed 403 solely to IP family. Source: https://exa.ai/docs/get-started/exa-mcp (Troubleshooting).
- Generic remote MCP OAuth browser authorization, discovery/registration and refresh are deferred. This pass offers keyless and API-key presets; do not show a sign-in action that is not implemented. Future managed short-lived access tokens must feed the existing credential-reference/header-binding model, not a competing auth representation. Preserve current unrelated OAuth connectors.
- Presets define their key as optional. Manual Bearer/API-key setup defaults the declaration to required; keep Required/Optional available under advanced credential configuration so the agreed expressive contract is preserved without making every owner decide upfront.
- User explicitly reaffirmed vault-backed temporary draft credentials and best-effort tool-result echo screening after a review proposed deferring them. R16-R18 remain acceptance requirements. Exact limits and import protections also remain.
- R21 audit destination: metadata-only `RemoteMcpAudit` structured events use the existing Gateway console/OTLP observability sinks and their configured retention/filtering. CredentialsHost retains its existing credential mutation integrity audit. No artificial Narad chat/run is created for a configuration action, and no new audit service is introduced. Audit event delivery is not a claim of tamper-proof or always-on retention independent of observability settings.
- `/workspace/specs/remote-mcp-configuration.md` referenced by the external reviewer is not present in this Windows checkout. This document remains the authoritative recorded spec.

### Intermediate implementation milestones (historical)

These milestones describe checks performed during development. The final compliance audit below supersedes their then-pending work notes.

- Chunk 1: structured contract, static validation, additive metadata column, both SQLite readers, store save/discovery/rollback and API/personal-URL projections reviewed. New required flag is explicit in source-generated JSON. Empty arrays clear configuration; omitted/null arrays preserve it. These declarations alone are not yet a functional transport implementation.
- Immediate local redirect fix: runtime automatic redirects disabled, replacing the old follows-redirect test with 301/302/303/307/308 custom-header non-forwarding tests. Combined targeted validation passed 9/9 tests. This is an independently reviewable local change; publication/commit has not been authorized.
- Chunk 2 foundation: runtime/test/discovery share a context-bearing Gateway handler; connector-local refs resolve at send, resolved limits apply before network activity, redirects fail, request headers are cleaned, and bounded per-response UTF8/JSON-escaped echo screening preserves SSE delivery without waiting for EOF. Foundation reviewed; credential lifecycle/invalidation remains subsequent work.
- Real TLS regression: two loopback socket tests passed, verifying hostname authority/SNI and rejecting a certificate-name mismatch through the existing pinning handler. Fixture imports only a generated test certificate for Windows Schannel; no production certificate-validation bypass was added.
- Frontend foundation: structured schema/draft/API normalization, import parsing and an independently tested remote editor added. Full frontend suite passed 509 tests across 112 files, with typecheck passing. Page/draft API integration and presets remain pending, so this is not yet a functional end-to-end UI acceptance claim.

### Subsequent verification and recovery review

- The final frontend suite passed 522 tests across 114 files after removal of the gate-era recovery panel. Type checking passed; lint had no errors and 29 warnings in unrelated files. Production build passed after remote setup, local/container setup and import dialogs were placed behind lazy-loading boundaries; the application entry chunk is 363.35 kB, below the existing 400 kB gate.
- Production-bundle Chromium checks passed at 1280×900 and 390×844: direct remote setup, masked credential input, initialize/list test result, opaque-reference save, no horizontal document overflow, and typed secret present only in the credential-administration request body. Synthetic API responses isolate these UI checks from user data and appliance runtime; they do not prove the live backend RPC path. The mobile screenshot was visually inspected.
- Provider contracts rechecked against primary documentation on 2026-11-05: [Tavily's FAQ](https://docs.tavily.com/documentation/keyless) confirms that a valid bearer API key takes precedence when the keyless header is also sent; [Exa's troubleshooting](https://exa.ai/docs/get-started/exa-mcp) documents the direct IPv4 condition for keyless access; [Firecrawl's official repository](https://github.com/firecrawl/firecrawl-mcp-server#hosted-mcp-keyless-free-tier) documents the three-tool anonymous endpoint and optional bearer API-key setup. These are documented contracts, distinct from the anonymous live results and from pending authenticated verification.
- Anonymous live verification on 2026-11-05 used the built `McpHttpClientFactory` context-bearing overload, `RemoteMcpRequestHandler`, the production direct socket/destination policy and MCP SDK. A standalone temporary harness supplied no credentials and invoked search after initialize/list: Tavily listed 5 tools and returned an Example Domain result; Firecrawl listed 3 and returned a successful web result; Exa listed 2 and returned an Example Domain result. These are component-level checks through the implemented transport, not a deployed appliance or live Gateway-to-CredentialsHost RPC test. No provider response body, request identifier or local credential is persisted in this document. Authenticated live verification remains pending securely supplied keys.
- Independent architecture review found that a partially completed activation could strand a connector gate after configuration edits or ID recreation. Following the user's questions about complexity and atomicity, activation was replaced: CredentialsHost stages fresh opaque credential associations without overwriting active associations, then Gateway commits the connector configuration and its selected association reference together in one local transaction. That commit is the activation point. Personal-only endpoint/credential edits also stage a scope/destination-preserving snapshot and switch the same internal association reference atomically; they do not change the shared public configuration or ownership. This avoids a separate personal-overlay mechanism in CredentialsHost. A failed configuration save leaves the previous association active; retries reuse the staged association idempotently. Cleanup never deletes active association entries. Exploratory activation gates, rollback journals, per-key mutation versions and administrator reconciliation APIs were removed. Unreferenced encrypted association versions are retained; safe automatic garbage collection is deferred. Temporary input still expires server-side and is cleaned independently.
- The initial CredentialsHost staging foundation passed two real-vault repository tests covering scope/destination-preserving carry-forward, immutable prior values, replacement/removal, idempotency, expiry separation, rotation and corrupt-envelope rollback. Review identified and corrected retention of nonsecret candidate metadata for an exact staged-save retry after temporary values expire. Subsequent owning-store/API tests establish atomic activation and the RPC contract with isolated service implementations; this is not live appliance verification.
- Production-factory/MCP-SDK loopback tests passed initialize, list, invocation and DELETE termination with final-send injection, screened descriptions/results, suppressed automatic cookie echo, and automatic/explicit protocol negotiation. Handler cases passed safe actionable HTTP 401/403/429 failures, no remote-body echo and no anonymous retry.
- Final-transport live checks again returned actual anonymous search results for Firecrawl and Exa. Tavily's earlier successful result was followed by a provider `monthly_cap_reached_bonus_eligible` response on further testing; initialize/list still succeeded. No payment, signup or bonus action was taken. The preset's usage-limit wording is intentional; discovery success does not establish available search quota.

### Baseline before implementation

- Checkout was clean on `main`; branch `enhancement/remote-mcp-configuration` was created before this document and implementation changes. No commits or pushes were made.
- Backend baseline: `dotnet test src/tests/Kautuka.IntegrationTests/Kautuka.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~Connector|FullyQualifiedName~Mcp|FullyQualifiedName~CredentialSecurityBoundary" --verbosity minimal` passed 298 tests, zero failures/skips (49 seconds).
- Frontend baseline: `pnpm --dir src/frontend/web test:run src/test/ConnectorFormModal.test.tsx src/test/ConnectorCard.managed.test.tsx src/test/ImportModal.test.tsx` passed 6 tests across 3 files. Dependency preparation changed installed packages but no tracked package/lockfile changes were produced.
- Developer delegation: user selected GPT-6.1 Terra, medium reasoning after requested GPT-6.1 Luna was rejected as unavailable. First chunk covers contracts, validation and persistence; later chunks require primary-agent review.

### Final compliance audit

The following evidence combines owning-source inspection with automated checks. Tests use synthetic credentials and isolated databases. Actual user credentials were not inspected, and the appliance was not deployed, reset or restarted. All feature acceptance checks passed. The full-suite live-model flake and conditional skips are reported separately below rather than represented as a completely green full run.

| Requirement | Owning implementation and verification | Status / practical limit |
| --- | --- | --- |
| R1 | `McpHttpClientFactory` owns direct HTTP transport; CredentialsHost repositories/RPCs only store, copy and resolve. Container execution stays in its existing paths. Architecture contracts and connector/mail regressions pass. | Verified scope; no new Narad/container transport dependency |
| R2 | Structured `RemoteMcpConfiguration`, both SQLite readers and topology writer; obsolete `McpHttpAuthHeaders` deleted; no production magic `MCP_AUTH_TOKEN` references. Existing generic/container credential storage is retained. | Verified; no legacy remote-auth migration/fallback |
| R3 | Trusted request context is `[JsonIgnore]` in API DTOs; runtime reconstructs current authorized connector/agent state. `CredentialSecurityBoundaryTests` and architecture contracts exercise capability boundaries. | Verified; agent arguments contain no credential resolution controls |
| R4 | `RemoteMcpRequestHandler` resolves only at send, injects individual headers and removes them in `finally`; factory has no fixed secret defaults/SDK headers. SDK and handler tests screen echoes; automatic cookie state disabled. | Verified within trusted execution; managed-string erasure and transformed echoes are not guaranteed |
| R5 | `RemoteMcpEditor`, connection dialog, direct page action and hostname-only identity helper. Frontend tests plus real production-bundle desktop/mobile Chromium checks. | Verified; ordinary setup requires no JSON/container fields |
| R6 | Credential/header records, explicit JSON-required boolean, validator, schema/draft payloads and store/read round trips; container arrays remain separate. | Verified one model |
| R7 | Static validator and import helper reject duplicate/controlled/invalid headers, undeclared or mixed sources and known sensitive literals. Configuration tests include custom API-key/token names. | Verified; arbitrary strings cannot be conclusively classified as secrets |
| R8 | Final-send UTF8 measurements enforce 20 / 256 / 8192 / 32768 limits after prefix resolution. Handler tests assert no value or prefix fragments in failures. | Verified |
| R9 | Required/optional/whitespace checks, invalid association and vault failure rejection; safe 401/403/429 messages and no anonymous retry. Handler and association tests distinguish valid empty sets from missing references. | Verified |
| R10 | Explicit UI mutations, blank preservation, scoped presence RPC, user/system fallback and destination-bound keys. Real API/repository tests cover replacement/removal and personal URL isolation. | Verified; encrypted old association versions are retained |
| R11 | Card/editor required/optional/free states are separate from test result. Input edits reset verification. Editor/card tests and production browser test exercise these states. | Verified; quota can change after discovery/test |
| R12 | Runtime and both test paths use the context-bearing factory/handler. Real SDK loopback covers initialize/list/call/DELETE, explicit/automatic protocol negotiation and credential injection. | Verified; no new client pool |
| R13 | Redirect-disabled socket handlers, five redirect status cases, origin restriction and provider-advertised URI validation in each send. Private/personal destination API tests remain. | Verified; no implicit proxy or cross-origin credential forwarding |
| R14 | Existing DNS-to-socket pinning retained. Real loopback TLS tests check actual socket address, hostname authority/SNI and certificate-name rejection. | Verified on Windows; no production TLS bypass |
| R15 | Domain revision/lease tracker, current-user interface checks, per-caller fingerprints and fresh clients. Barrier tests cover user/system/header changes, unrelated-user eligibility and admin demotion; scoped owner update preserves shared timestamp/other lease. Independent review confirmed the snapshot race fixed. | Verified; no uncertain tool-call replay |
| R16 | Bounded per-response UTF8 and JSON-escaped stream screening, generic remote errors, metadata-only logging and no cookie cache. Stream/quote/boundary/SDK tests exercise results and descriptions. | Verified best effort; malicious transformations remain outside the guarantee |
| R17 | Vault-first create API, opaque test/save calls, initialize/list-only test service, bound draft/source metadata, 900-second server expiry, quota five and cancellation. Repository/API/lease/browser tests cover expiry and ownership. | Verified; staged durable records are distinct from expiring temporary input |
| R18 | Immutable CH staging plus one Gateway config/reference/operation write; scoped pointer-only writes use the same connector mutex. Real store/CH tests cover Manager rejection, restart readers, TTL retry, no restage, stale/recreated IDs, permission loss and cancel/save ordering. Explicit cancel removes retry metadata, never active association entries. | Verified; uncertain unreferenced versions remain encrypted until safe GC exists |
| R19 | Save stages locally without invoking provider tools; draft tests do not write discovery. New configuration activation clears discovery atomically. Form failure/retry and current permission tests remain. | Verified; provider availability is separate from configuration success |
| R20 | Common remote JSON import preserves headers, extracts recognized secrets to masked form mutations, blocks URL credentials/placeholders/unsupported fields, and checks replacement permission. Uses the same remote editor. | Verified frontend/import coverage; no live imported secret used |
| R21 | Existing Gateway `RemoteMcpAudit` structured sink plus CH credential audit; actual draft config writes and per-key staged mutations have metadata events. Unit/API/repository tests check actor/scope/action and omitted values. Existing probe semaphore retained. | Verified; retention depends on configured observability sinks |
| R22 | Three ordinary disabled presets, optional bindings and durable seed/removal receipts. SQLite restart test preserves edits, ownership and deletion; provider contracts checked against primary docs. | Verified; no agent access granted automatically |
| R23 | Frontend 522/522, 114 files; typecheck, lint (zero errors), production build and two browser tests pass. Contract suite 84/84 and security suite 35/35 pass after structural fixes. Full integration: 2060 total, 2055 passed, four conditional skips, one unrelated direct live-Ollama assertion failed and then passed in isolation without code changes. | Verified with the explicitly reported live-model flake; no CI/publication claim |
| R24 | Anonymous production-transport initialize/list/search succeeded for all three providers. Latest Firecrawl/Exa payload checks confirm actual result URLs; later Tavily searches reached its documented keyless cap while discovery remained available. | Authenticated live checks pending securely supplied keys, as R24 permits; no live appliance/Gateway-to-CH deployment verification claimed |

### Final validation record

- `dotnet test src/tests/Kautuka.IntegrationTests/Kautuka.IntegrationTests.csproj --no-build --no-restore --verbosity minimal`: 2060 tests, 2055 passed, four skipped, one failed, 6m24s. Every remote MCP, schema, container, mail and Narad test passed in this final full run. The failing `PackedToolCallAttributionTests.NativeOllamaChatPacksIndependentToolCalls` sends a direct request to the existing local Ollama service and asserts exactly three model-emitted calls; the model returned one. It does not exercise remote MCP configuration and its source was unchanged. Rerunning the entire `PackedToolCallAttributionTests` group without code changes passed ten tests, with its other live-model comparison skipped under that test's own condition. No model, Ollama configuration or unrelated production code was changed to force this result.
- The four full-run skips are the existing Linux `openat2` race and Unix file-mode checks on Windows, the absent isolated broker Worker fixture, and the unavailable local sandbox image. No image was pulled or runtime reprovisioned to bypass these conditions.
- Full `Kautuka.ContractTests`: 84 passed. Full `Kautuka.SecurityTests`: 35 passed. No architecture policy suppressions were added; endpoints use the existing user-store interface and the revision tracker lives in the domain layer.
- Latest focused production-factory/TLS/role/scoped-update run: 11 passed. Earlier isolation of schema preservation, incompatible column rejection, container probe capture, Narad/mail timing cases and association/SDK behavior: 16 passed. The schema correction only accommodates the exact new column declaration's SQLite append position; other schema validation rules remain. Container test capture now selects the intended connector rather than a background probe, retaining all environment assertions.
- After the full run, the only production addition was the metadata-only creation audit event for newly seeded presets. Its focused preset/audit selection rebuilt successfully and passed three tests. No additional transport, credential or runtime behavior changed after the full run.
- Frontend `pnpm test:run`: 522 tests in 114 files passed. `pnpm type-check` passed. `pnpm lint` passed with zero errors and 29 warnings in unrelated files. `pnpm build` passed the existing application chunk gate (363.35 kB entry). `pnpm exec playwright test e2e/remote-mcp-setup.spec.ts`: two production-bundle Chromium cases passed, desktop and mobile.
- `git diff --check` passed. The branch remains `enhancement/remote-mcp-configuration`; all work is local and uncommitted. There is no GitHub CI, deployment, authentication-key live test or running-appliance certification claim.

Known limits retained by design: encrypted unreferenced association versions await safe GC; known-echo screening is best effort; configured observability controls audit retention; provider free quotas can be exhausted; generic remote OAuth sign-in is deferred. These do not remove the agreed temporary-input TTL, final-send injection, atomic activation, scope isolation or credential exposure boundary.
