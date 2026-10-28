Kautuka first-round security code review — 9 October 2026
======================================================

Runtime follow-up: [unauthenticated assessment](security-runtime-review-2026-10-09.md) found no account/data-access bypass in the tested requests, reproduced finding 8 (shared authentication rate-limit exhaustion), and identified a Low null-input validation defect. Findings below remain source-review findings unless explicitly runtime-confirmed there.

The current working tree contains nine actionable findings: six High and three Medium. The most urgent are credential disclosure through connector testing, filesystem escape through links, and cross-user access to system-agent files. Treat these as release blockers for use by mutually untrusted users.

Scope: source review of the current working tree based on commit `683854f623eb238fecafabd78080f314cdf99a68`, including existing modified and untracked external-login files. Findings about those files describe work in progress, not necessarily a released version. No production endpoints, real credentials, or external accounts were attacked. This is a first-round review, not an exhaustive penetration test or certification. Severity reflects plausible confidentiality, integrity, and availability impact with the prerequisites below; these are qualitative ratings, not calculated CVSS scores.

Validation: `dotnet test src/tests/Kautuka.SecurityTests/Kautuka.SecurityTests.csproj --no-restore --verbosity quiet` passed all 27 tests. A separate source-linked harness exercised the actual AgentFilePathAuthorizationService with stubbed catalog/workspace dependencies and an already-authorized user. A junction entirely within synthetic fixture directories passed authorization; normal file IO then read and overwrote a marker outside the authorized workspace. This validates the path-boundary defect, not a complete HTTP or Linux container exploit. No application source was changed.

1. **High — Connector testing can disclose stored system credentials.**

   Evidence: [connector test endpoint and credential merge](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Endpoints/Connectors/GatewayConnectorEndpointMappings.cs:304), [credential resolution](D:/Sources/Kautuka/src/backend/Kautuka.CredentialsHost/Grpc/CredentialsGrpcService.cs:54), [HTTP Authorization header construction](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Connectors/McpHttpAuthHeaders.cs:7).

   `/api/connectors/test` requires an authenticated user, but accepts caller-supplied EnvKeys, HttpUrl, transport, and process arguments. When no accessible connector is resolved, the supplied key list still reaches GetResolvedAsync. That service decrypts system credentials as well as the caller's own credentials. The returned values are injected into the caller-selected test transport. Even with an accessible connector ID, a caller-supplied destination is retained rather than bound to the stored connector. RemoveRefreshTokens only filters credentials for a recognized profile, so an absent or unrelated ID is not a general safeguard.

   Prerequisites and impact: an ordinary active account and a stored key of interest. A system MCP_AUTH_TOKEN can be sent as an Authorization header to an attacker-controlled HTTP receiver. Other requested keys can be exposed by a caller-controlled container probe, subject to runtime availability. This does not provide direct access to other users' user-scoped credentials; the confirmed resolution scope is the caller plus system fallback.

   Remediation: resolve stored secrets only for an authorized, persisted connector and its approved destination/runtime definition. Do not accept arbitrary key names alongside arbitrary executable configuration. Use user-supplied temporary secrets for unsaved test configurations. Enforce connector-specific secret grants in the credential service, and make held OAuth credentials non-exportable independent of the requested connector ID.

   Regression check: a low-privilege account with an invented connector ID and a synthetic system key must fail before any outbound request or process start; changing a known connector's URL must not carry stored secrets.

2. **High — Workspace paths can escape through symbolic links or junctions.**

   Evidence: [lexical authorization](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/AgentFiles/AgentFilePathAuthorizationService.cs:53), [download](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/AgentFiles/AgentFileExchangeService.cs:210), [upload](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/AgentFiles/AgentFileExchangeService.cs:165), [recursive enumeration](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/AgentFiles/AgentFileExchangeService.cs:440).

   The path guard rejects traversal text and checks GetFullPath against the workspace prefix. It never rejects links in the filesystem path. File.OpenRead and FileStream follow those links. Parent-directory links affect uploads too. A link can also hide a `.kautuka` target behind an allowed name. Recursive directory summaries follow child directories without a link or cycle guard.

   Prerequisites and impact: the attacker can cause a link to exist in an accessible workspace, for example through permitted sandbox command execution. Requests then reach files accessible to the Gateway process outside that workspace, potentially including other users' files or Gateway state. The exact reach depends on mounts and OS permissions; this is not a demonstrated host-root escape. Cyclic links also create an availability risk.

   Validation output: `LINK_AUTHORIZATION=Success`, `LINK_READ=synthetic-outside-marker`, `OUTSIDE_WRITE=synthetic-write-through-link`; ordinary `../` traversal returned `InvalidPath`.

   Remediation: use filesystem operations anchored to an opened workspace directory with beneath-root/no-follow semantics. Reject reparse points/symlinks in every component and the final target; prevent check/open races rather than relying only on a preliminary canonicalization. Apply equivalent rules to writes, downloads, attachment handling, and traversal. Bound recursion and track visited directories.

3. **High — System-agent workspaces expose one user's attachments to another.**

   Evidence: [system-agent access rule](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Auth/GatewayOwnershipAccess.cs:13), [workspace resolution](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Infrastructure/Sandbox/WorkspaceRuntimeContextResolver.cs:342), [agent owner selection](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Infrastructure/Sandbox/WorkspaceRuntimeContextResolver.cs:409), [attachment storage](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Assets/ConversationAssetStore.cs:790), [file authorization](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/AgentFiles/AgentFilePathAuthorizationService.cs:17).

   Every authenticated user can access a system-scoped agent. ResolveAgentWorkspace receives userId but does not use it to construct HostWorkspacePath: the path comes from the agent's owner/name. Conversation attachments are placed under that same workspace's attachments directory. The file API authorizes access to the agent, not the attachment's owning session, and permits uploads/overwrites there as well.

   Prerequisites and impact: two users use the same system agent, with file exchange enabled. User B can list/download user A's workspace attachments through the agent-files API and can alter shared files used by A's runs. Session ownership checks do not protect this alternate file route.

   Remediation: derive execution and attachment paths from immutable user and agent IDs, and enforce session/asset ownership on every access path. Put intentionally shared documents in a separately authorized shared area; do not silently place private chat uploads there. Audit existing shared directories before migration. Avoid display-name-derived paths, which also permit normalization collisions.

   Regression check: two ordinary users accessing the same system agent must get separate private roots; B must not list, download, overwrite, or consume A's attachment.

4. **High, deployment-dependent — Microsoft email-based auto-linking can attach an external identity to the wrong account.**

   Evidence: [Microsoft claim validation](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Auth/ExternalIdentityToken.cs:164), [automatic account binding](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Stores/GatewayUserStore.External.cs:106). These are currently untracked implementation files.

   Microsoft tokens are checked for signature, audience, nonce, expiration, and issuer/tenant consistency. However, any syntactically matching tenant is accepted, and the email claim is then treated as authority to bind to any single active local account with that email. TryBindInvitedUser does not require an invitation secret, existing-account reauthentication, or independent email ownership proof. An account without an existing Microsoft link is eligible, including an administrator with an email address.

   Microsoft documents that the ID-token email claim is mutable and not guaranteed correct, and must not authorize access. See [Microsoft ID token claims reference](https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference).

   Prerequisites and impact: Microsoft login enabled, the upstream registration/broker admitting the relevant identity, and a valid token whose email matches an unlinked local account. This can become account takeover. The identity-provider configuration and a real-provider exploit were not tested; the source defect is confirmed, while exploitability is conditional.

   Remediation: link only after authenticated account-owner confirmation or redemption of a single-use invitation bound to the intended identity. Persist issuer plus subject, or tenant plus object ID, as the identity key. Enforce intended tenant restrictions. Do not replace this with a check for an assumed Microsoft email_verified claim.

5. **High, deployment-dependent — First reachable caller can claim the initial administrator.**

   Evidence: [anonymous setup handler](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Endpoints/Auth/GatewayAuthEndpointMappings.cs:38), [admin creation](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Stores/GatewayUserStore.cs:31), [social setup path](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Stores/GatewayUserStore.External.cs:18).

   Initial admin creation checks only that setup has not completed; no install-specific possession secret or local administrative confirmation is required. Social sign-in also creates an admin while setup is incomplete. The transactional setup guard correctly prevents two winners, but it does not ensure the rightful operator wins.

   Prerequisites and impact: an uninitialized instance reachable by the attacker, such as a TLS-enabled LAN deployment exposed before onboarding, or another local user able to reach the loopback listener. Default WebHost loopback binding reduces remote exposure. This is not an authentication bypass after setup is complete.

   Remediation: require a random, single-use bootstrap token delivered through a protected local installation channel; require it for both password and social setup. Keep external access closed until the token is redeemed and setup finishes.

6. **High — Connector probes bypass import permissions and expose backend network access.**

   Evidence: [test handler](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Endpoints/Connectors/GatewayConnectorEndpointMappings.cs:12), [import permission check for comparison](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Endpoints/Connectors/GatewayConnectorEndpointMappings.cs:60), [probe dispatch](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Connectors/McpConnectorTestService.cs:14), [command mapping](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Connectors/McpCommandTransportMapper.cs:15), [Manager execution](D:/Sources/Kautuka/src/backend/Kautuka.Manager/Grpc/WorkspaceGrpcService.cs:464).

   A user denied connector import can still submit arbitrary test definitions. Node/Python command arguments reach a container execution path. The HTTP path accepts an absolute URL and opens the MCP transport from the Gateway, with no application-level destination allowlist or private-address restriction in this path. Naming the operation a test does not prevent connector startup code from executing.

   Prerequisites and impact: an ordinary account; a functioning Manager/runtime for command execution. The caller gains unauthorized sandbox execution, resource consumption, and backend-originated network probes. Container execution is subject to existing non-root/capability/resource restrictions; this is not host code execution. HTTP responses are interpreted as MCP, so this is not a demonstrated arbitrary-response SSRF data dump.

   Remediation: require import/create permission for arbitrary test definitions. For saved connectors, authorize the exact connector and derive its definition server-side. Apply explicit outbound network policy, including DNS resolution and redirects, while allowing administrator-approved private MCP services. Bound probe concurrency and duration. Fix this independently of credential export in finding 1.

7. **Retracted — The reported loopback shutdown authorization bypass was not supported by the implementation.**

   The original review missed the AdminManagement authorization policy on the Gateway shutdown and desired-state rehydrate endpoints. Both require an authenticated administrator as well as a loopback connection. The Windows desktop host's hard-coded loopback shutdown request carries no credentials, so the Gateway rejects it; the host then falls back to terminating its child process. This is a graceful-shutdown integration defect, not an unauthenticated shutdown bypass.

   Evidence: [management endpoint mappings](../../src/backend/Kautuka.Gateway/Endpoints/Internal/GatewayManagementEndpointMappings.cs), [security validation](security-validation-2026-10-12.md).

8. **Medium — Proxy users share one authentication rate-limit bucket.**

   Evidence: [limiter partition](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Hosting/Modules/GatewayModules.cs:90), [forwarded headers](D:/Sources/Kautuka/src/backend/Kautuka.WebHost/GatewayProxy.cs:65), [Gateway middleware](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Hosting/GatewayAppBootstrap.cs:390).

   WebHost sets X-Forwarded-For, but Gateway does not process forwarded headers before partitioning by Connection.RemoteIpAddress. All browsers behind the proxy therefore share its address, or `unknown` for a socket. The default budget is ten requests per minute, shared by login, refresh, setup, and external-login operations.

   Prerequisites and impact: access to proxied authentication endpoints; no account required. One caller can repeatedly consume the shared budget and deny other users login or refresh. Normal multi-user activity can also hit this limit.

   Remediation: enforce client limits at WebHost or propagate and validate identity through an explicitly trusted proxy boundary. Add separate account and operation budgets. Never trust arbitrary client-supplied X-Forwarded-For, and preserve strict protection of management routes when introducing forwarding support.

9. **Medium — Connector read access grants unauthorized discovery metadata writes.**

   Evidence: [discovery endpoint](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Endpoints/Connectors/GatewayConnectorEndpointMappings.cs:157), [persistent mutation](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Domain/Stores/GatewayCatalogStore.McpServers.cs:252), [approval identity use](D:/Sources/Kautuka/src/backend/Kautuka.Gateway/Infrastructure/Agents/HitlApprovalIdentity.cs:57).

   PUT `/api/connectors/{serverId}/discovery` checks CanAccessConnector, while the normal update endpoint checks CanManageConnector. All users can access system connectors, so they can persist arbitrary tool counts/names into shared connector definitions. Those names also participate in fallback approval identity lookup. This confirms shared metadata tampering; an approval-policy bypass has not been demonstrated.

   Remediation: require management permission for persistent discovery updates, preferably derive them from a server-side probe of the approved connector rather than trusting caller-supplied tool names. Keep low-privilege test output transient. Regression-test ordinary-user writes to system connectors for 403.

Suggested implementation order: close connector credential/probe authority first; fix file boundaries and separate private workspaces next; correct external identity linking before enabling it; then bootstrap possession, management routes, limiter partitioning, and discovery authorization. Add negative tests for each boundary, not only successful-owner flows.

Existing controls verified in source include authentication fallback, active-user checks, hashed passwords, token revocation generations, encrypted credential envelopes, non-root constrained connector containers, TLS requirements on non-loopback WebHost bindings, and signature/digest checks in update handling. These reduce risk but do not close the findings above. Old Gateway channel HTTP endpoint definitions are not registered by GatewayChannelsModule and were excluded from reachable findings.

Remaining work for a second round: exercise full HTTP reproductions with two disposable users on Linux/WSL, inspect effective container mounts and network egress, verify real identity-provider tenant settings, assess channel gRPC delegation/replay end to end, test archive/link races and resource exhaustion, and run a dependency/advisory audit. No conclusion of absence of vulnerabilities is implied for areas not fully traced.

