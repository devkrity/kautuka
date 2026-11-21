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

The development stack launcher waits for each service's listener. On Linux it connects to the Manager Unix socket, so a stale socket file left by a crashed process is not considered ready. Manager initializes the NAC topology before that listener is bound; if NAC initialization leaves the startup gate closed, the Manager host aborts in Development and the launcher fails instead of reporting a running stack. Production retains its fail-closed workload gate while Manager stays available for diagnosis and recovery. This check covers Manager's NAC startup gate and listener only; it does not wait for workspace reconciliation or sidecar readiness.

## Runtime image pins

The runtime image manifest is the source for the egress boundary, Envoy, and CoreDNS pins. Envoy and CoreDNS have separate x64 and ARM64 image references; the existing release archives contain the x64 images. Manager embeds the manifest at build time, and release tooling reads the same source file.

## Container network

Agent sandboxes, connector containers, package builders, and the package proxy join `kautuka-workload`; the egress pod connects that network to `kautuka-outside`. Other sidecars use their configured sidecar networks. Managed workload traffic goes through the NAC boundary and Envoy, with Manager deciding access. Isolation remains a private container plus optional read-only agent-file mounts; it is not `--network none`. MCPB language-package installation runs in a temporary proxy-only workload and is committed to an image; its package-proxy hostname maps to the NAC service address. System-layer `podman build` is a separate build path outside that workload network. Install CLIs pin `--registry` / `--index-url` so a bundle `.npmrc` or `pip.conf` cannot retarget public indexes. Runtime `npx`/`npm`/`uv`/`uvx` connector launches pin those same flags from Gateway index environment. Gateway strips `PIP_EXTRA_INDEX_URL`, `UV_EXTRA_INDEX_URL`, and `UV_INDEX`, and skill exec cannot overlay package-index keys. `uv` may still download a managed CPython from GitHub when a bundle requires a Python the Fedora image does not ship; that is an interpreter bootstrap, not an npm/PyPI package. The agent sandbox base Dockerfile includes a checksum-pinned `uv` binary and sets `USER node`; Manager builds it directly without a separate package-proxy install. `ManagerProxy:Mode=upstream` and public package-index hosts (`registry.npmjs.org`, `pypi.org`) are refused for managed package configuration. Gateway connector tests do not `GET` the public npm registry from the host. RPM/microdnf and `nodejs.org` tarball fetches stay out of pkgs-proxy. Host/dev pnpm, NuGet, and OCI pulls are not pkgs-proxy traffic.

The native development launcher stages the built-in `kautuka-web-tools` MCPB before service startup using the platform-native build script. If staging fails, it warns and continues with any last-known-good artifact. In Development, Gateway checks the repository's versioned MCPB artifact before bundles copied into the app output, so a fresh bundle is imported on the next startup; production bundle precedence is unchanged.

If a connector test times out while its MCPB image is being built, the build continues and a later test can reuse the completed image. The timed-out test does not start the connector after the build completes. Manager shutdown or closure of workload launch admission can cancel the build.

MCPB import validation runs with the identity of the user importing the connector.
MCPB inspection reads any tool names declared in the bundle manifest; the bundled Web Tools manifest includes its tool name.
Built-in bundle registration uses supplied tool metadata and does not run userless connector discovery at startup.
On a fresh installation the bundled connectors start disabled. An administrator enables each connector, which runs the existing image build and tool discovery during save; existing enabled or disabled choices survive a restart or upgrade.
When an administrator signs in, the open application checks for enabled bundled connectors whose saved tools are missing or outdated and refreshes them in the background using the administrator's identity. It retries once; if refresh still fails, Test connection remains available on the connector.
Local connector tool definitions are cached when the connector is saved or tested; an agent run uses those definitions rather than listing the local connector tools again.

Built-in mail connectors are available systemwide and use the invoking user's network permissions. A denied Gmail or Microsoft Graph request can therefore appear as a network access request for approval.

Mail sign-in checks access to the configured sign-in service and asks the user before allowing it for that connector. After sign-in, it asks separately about the mail provider's API when needed. Declining the second request leaves the mailbox signed in but the provider connection blocked.

After an approved tool call runs, its journal records an error when the returned result reports a tool failure, including MCP's `is_error` result; approval itself does not imply that execution succeeded.

Administrators can load live package-proxy cache inventory on demand at `GET /api/runtime/diagnostics/pkgs-proxy/stats` (`AdminDiagnostics`). Gateway asks Manager to `podman exec kautuka-pkgs-proxy proxy stats -json`; Kautuka does not persist that payload. The host port stays unpublished.

## State roots

`KautukaHostPathPolicy` selects a user-scoped development root and `/var/lib/kautuka` in production, then derives service state directories. Do not construct service paths independently.

Sidecar catalogs use `/var/lib/kautuka` as the production host-volume root. On Linux development, Manager maps volumes beneath that root into the configured shared data root; paths outside it remain unchanged. Linux production keeps catalog paths as configured, including when `ContainerStateRoot` overrides the shared data root. Windows retains its existing override remapping behavior.

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

Linux update recovery snapshots only the supported SQLite databases and matching authentication/certificate keys after their host writers stop. A durable maintenance fence blocks application requests, dispatch, channel polling, workflow execution, and Manager reconciliation until target readiness is verified and UpdateService commits. An uncommitted failure restores those databases, keys, and service pointers from the operation-bound verified snapshot; a reboot repeats that recovery safely. Once committed, updates are repaired forward. There is no user-facing release rollback path. Workspace files, attachments, container state, images, and other file stores are outside the snapshot and are never restored by update recovery. Legacy helpers and unsupported custom production roots fail closed and require a current signed installer bootstrap.

Accepting connector desired state does not build connector images. Manager materializes the required runtime and sandbox images when a connector is used, reusing content-addressed local images and serializing concurrent builds. A failed first-use build is reported to that operation; it does not prevent Gateway from starting.

## Provider types

Gateway registrations currently expose Ollama, Gemini, OpenAI, OpenAI Compatible (Responses), Azure OpenAI, Foundry, Anthropic, Groq, Mistral, and OpenRouter. The provider type catalog controls default URLs and credential requirements. Provider credentials are resolved separately from catalog configuration.

Ollama runs through OllamaSharp against the native `/api/chat` endpoint (not the OpenAI `/v1` shim) so thinking maps to MEAI `TextReasoningContent` and tool results serialize under OllamaSharp’s AOT JSON context. Gemini uses `Google.GenAI` against the Gemini Developer API (not `/v1beta/openai/`) so thought signatures stay on MEAI `IChatClient` for tool follow-up turns. Saved OpenAI-compat Gemini base URLs are ignored. Google.GenAI has no parallel-tool FunctionCallingConfig flag (only AUTO/ANY/NONE); when `AllowMultipleToolCalls` is on, Gateway appends one system-context sentence so Gemini will emit independent calls in one turn. The Developer API rejects blob `displayName`; Gateway strips MEAI `DataContent.Name` / `HostedFileContent.Name` on the Gemini chat client so image parts keep bytes and mime type. Before MAF session serialize, Gateway clears ephemeral `AgentRequestMessageSourceAttribution` tags that are missing from MAF’s source-gen context under Native AOT.

## Configuration

Runtime platform configuration is stored in SQLite normalized tables. Host bootstrap needs a small set of environment/configuration inputs before SQLite can be opened, including data roots, listen/TLS settings, and provisioned update source paths. Do not introduce `appsettings` fallback for domain settings.

## Schema policy

Fresh empty stores receive the current embedded baseline. Non-empty stores must match the current schema exactly and fail otherwise. Removed fields/tables are not maintained for backward compatibility unless an explicit future decision says otherwise.
