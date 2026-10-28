# Runtime reference

## Fixed ports

| Process | Port | Protocol |
|---|---:|---|
| Gateway | 20000 | HTTP by default; remote listen requires configured TLS |
| Manager | 20001 | HTTPS HTTP/2 mTLS gRPC |
| CredentialsHost | 20002 | HTTPS HTTP/2 mTLS gRPC |
| ChannelHost | 20003 | HTTPS HTTP/2 mTLS gRPC |
| UpdateService | 20004 | HTTPS HTTP/2 mTLS gRPC |
| WebHost | 20229 | Loopback HTTP by default; non-loopback requires configured HTTPS |

These values are code-owned by `KautukaHostNetworkPolicy`.

Gateway exposes unauthenticated `GET /api/runtime/system-health` so first-boot and the installer can see Manager reachability (`lifecycle.coreReady`, JSON field `workspace`) before any admin exists. The fallback authorization policy still requires a bearer token for every other `/api/runtime/*` route. Authenticated `GET /api/runtime/execution-readiness` is the canonical preflight view for agent execution. It probes Manager reachability, Manager-reported Podman availability, and CredentialsHost in parallel. Manager reachability is the status RPC itself; Podman availability is Manager's last-known sample, kept warm by a background loop rather than a live `podman info` on each status or chat call. Desired-state reconciliation is not a dependency outage. Podman failures are returned as the distinct `container_runtime` dependency. New runs and approved continuations fail before runtime construction when a required dependency is unavailable; terminal deny, cancel, and stop decisions bypass the execution preflight.

## Container network

Manager creates the `kautuka-sandbox` Podman network before reconcile. Agent sandboxes, connector containers (including Isolated / OwnContext), and sidecars all run with `--network kautuka-sandbox`. Sidecars publish `--network-alias` names used as package-proxy hostnames (`kautuka-pkgs-proxy`). Isolation remains a private container plus optional read-only agent-file mounts; it is not `--network none`. MCPB materialization installs npm/pip/uv packages the same way: `podman build` only lays down OS packages and the checksummed Node tarball; language-package install runs as `podman run --network kautuka-sandbox` with Gateway index environment, then `podman commit`. Install CLIs pin `--registry` / `--index-url` so a bundle `.npmrc` or `pip.conf` cannot retarget public indexes. Runtime `npx`/`npm`/`uv`/`uvx` connector launches pin those same flags from Gateway index environment. Gateway strips `PIP_EXTRA_INDEX_URL`, `UV_EXTRA_INDEX_URL`, and `UV_INDEX`, and skill exec cannot overlay package-index keys. `uv` may still download a managed CPython from GitHub when a bundle requires a Python the Fedora image does not ship; that is an interpreter bootstrap, not an npm/PyPI package. The agent sandbox image follows that split for the `uv` wheel: Dockerfile `podman build` does not `pip install`; Manager then installs `uv==0.11.19` on `kautuka-sandbox` through `kautuka-pkgs-proxy` and stamps `USER node`. Rootless `podman build --network` cannot join that network, so a build-time `RUN npm/pip/uv` would hit public indexes. Missing sandbox network or missing index URLs fail closed; `ManagerProxy:Mode=upstream` and public hosts (`registry.npmjs.org`, `pypi.org`) are refused. Gateway connector tests do not `GET` the public npm registry from the host. RPM/microdnf and `nodejs.org` tarball fetches stay out of pkgs-proxy. Host/dev pnpm, NuGet, and OCI pulls are not pkgs-proxy traffic.

Administrators can load live package-proxy cache inventory on demand at `GET /api/runtime/diagnostics/pkgs-proxy/stats` (`AdminDiagnostics`). Gateway asks Manager to `podman exec kautuka-pkgs-proxy proxy stats -json`; Kautuka does not persist that payload. The host port stays unpublished.

## State roots

`KautukaHostPathPolicy` selects a user-scoped development root and `/var/lib/kautuka` in production, then derives service state directories. Do not construct service paths independently.

Important files include:

- shared `kautuka.db`;
- `gateway-auth.key` beside Gateway state;
- `agents/<storage-locator>/agent.db` per agent;
- `kautuka-usage.db`;
- CredentialsHost `kautuka.credentials.db`, key ring, recovery/transport material;
- ChannelHost `channelhost.db`;
- Manager `manager-desired-state.json`;
- UpdateService state/catalog/history JSON, written by unique same-directory temporary files, flushed to stable storage, and atomically replaced;
- workspace directories and attachment files.

Rollback backups explicitly include `gateway-auth.key` with `kautuka.db` and `kautuka.key`. Restore replaces the signing key with the backed-up identity so access-token verification remains consistent with the restored authentication state.

## Provider types

Gateway registrations currently expose Ollama, Gemini, OpenAI, OpenAI Compatible (Responses), Azure OpenAI, Foundry, Anthropic, Groq, Mistral, and OpenRouter. The provider type catalog controls default URLs and credential requirements. Provider credentials are resolved separately from catalog configuration.

Ollama runs through OllamaSharp against the native `/api/chat` endpoint (not the OpenAI `/v1` shim) so thinking maps to MEAI `TextReasoningContent` and tool results serialize under OllamaSharp’s AOT JSON context. Gemini uses `Google.GenAI` against the Gemini Developer API (not `/v1beta/openai/`) so thought signatures stay on MEAI `IChatClient` for tool follow-up turns. Saved OpenAI-compat Gemini base URLs are ignored. Google.GenAI has no parallel-tool FunctionCallingConfig flag (only AUTO/ANY/NONE); when `AllowMultipleToolCalls` is on, Gateway appends one system-context sentence so Gemini will emit independent calls in one turn. The Developer API rejects blob `displayName`; Gateway strips MEAI `DataContent.Name` / `HostedFileContent.Name` on the Gemini chat client so image parts keep bytes and mime type. Before MAF session serialize, Gateway clears ephemeral `AgentRequestMessageSourceAttribution` tags that are missing from MAF’s source-gen context under Native AOT.

## Configuration

Runtime platform configuration is stored in SQLite normalized tables. Host bootstrap needs a small set of environment/configuration inputs before SQLite can be opened, including data roots, listen/TLS settings, and provisioned update source paths. Do not introduce `appsettings` fallback for domain settings.

## Schema policy

Fresh empty stores receive the current embedded baseline. Non-empty stores must match the current schema exactly and fail otherwise. Removed fields/tables are not maintained for backward compatibility unless an explicit future decision says otherwise.
