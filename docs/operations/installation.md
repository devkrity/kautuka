# Installation

## Development

Use [Development workflow](../development/workflow.md) for a source checkout. Development state is user-scoped and separate from production appliance state.

## Windows appliance

`Kautuka.Installer.exe` is the supported Windows entry point. It checks/enables WSL prerequisites, preserves reboot-resume state, resolves a release, imports the appliance into the selected Windows directory, starts the appliance, and waits for `http://localhost:20229` before reporting success.

After installation, the Windows installer displays the one-time setup token in its local window and waits for the user to copy it and acknowledge that it is needed to create the first administrator in Kautuka's setup page. The token is deleted after successful use. If the installer window was closed, run `wsl.exe -d <distro-name> -u root -- cat /var/lib/kautuka/setup-token` in PowerShell, substituting the selected distro name.

The selected Windows directory stores the WSL distribution. Runtime paths remain inside the imported appliance. Uninstall unregisters the distribution and may remove the selected data when requested.

Select a location protected by BitLocker when agent files, uploaded attachments, or conversation data require encryption at rest. Kautuka does not application-encrypt workspace files and cannot infer whether the containing Windows volume is protected.

## Linux payload

Release scripts build a platform archive and installer from `scripts/kautuka-install-linux.sh.in`. The installed layout uses immutable version directories with `current` links under `/opt/kautuka/<service>`, a dedicated `kautuka` appliance account for Manager and rootless Podman, and production state under `/var/lib/kautuka`.

Production Gateway, ChannelHost, and CredentialsHost do not run in that shared user session. The installer creates the locked system principals `kautuka-gateway`, `kautuka-channelhost`, and `kautuka-credentials` *before* writing `/usr/lib/tmpfiles.d/kautuka-sockets.conf`, so `/run/kautuka/<service>` exists at 2750 when ProtectSystem units start. Starting CredentialsHost with a missing socket directory fails at systemd NAMESPACE, not later at bind(). Gateway-persisted MCPB archives live at `/var/lib/kautuka/mcpb-bundles` (`2750` `kautuka-gateway:kautuka`, files `0640`) so Manager can materialize built-in web tools. The installer does not write `/etc/kautuka/update-source.json` when every manifest URL is empty.

Channel trust material lives under `/var/lib/kautuka/security/channel`:

- `gateway-private` is `0700` and owned by `kautuka-gateway`; its two PFX files are `0600`.
- `gateway-trust` contains only the public channel CA and is read-only to `kautuka-gateway`.
- `channel-private` is `0700` and owned by `kautuka-channelhost`; the server PFX is `0600`.
- `channel-trust` contains only the public CA, Gateway client certificate, and delegation certificate and is read-only to `kautuka-channelhost`.

The bootstrap CA private key exists only in a root-owned temporary staging directory and is deleted by the released ChannelHost provisioning command. No runtime service receives it. On upgrade, the installer preserves the four inventories and validates exact filenames, owners, and modes before starting either service. A partial inventory, unexpected file, ownership drift, or relaxed private-key permission stops installation; the installer does not silently regenerate identity material. Repair the ownership or restore the complete inventory from a protected backup before retrying.

The installer assigns the shared Gateway SQLite database and any existing WAL/SHM files to `kautuka-gateway` before starting Gateway. Gateway then applies owner-only permissions to those files. Rootless Podman sidecar bind mounts use private SELinux relabeling (`:Z`) so Fedora's enforcing policy permits the container to write its own data without disabling container isolation.

The source-checkout convenience bootstrap is Development-only. It may create a shared local certificate layout for a single developer account, but production system units reject that fallback and require the four installer-owned inventories above. Do not copy development certificate directories into an appliance or set a production service to `Development` to bypass preflight.

Before stopping an existing installation, the Linux installer uses the `kautuka` account's rootless Podman to download any missing digest-pinned runtime images from the signed release payload, including the separately released Kautuka base, OpenObserve, and package-proxy mirror. It builds the small local package-proxy wrapper from the pinned Devkrity GHCR image. Podman displays download/build output in the terminal. Development payloads without a published Kautuka base fall back to pulling the Fedora base declared in the bundled sandbox Dockerfile. At startup, Manager starts the sidecars and verifies or prepares the Kautuka sandbox image through the package proxy; it does not report its runtime ready until this succeeds. The installer then waits up to ten minutes for the Web UI, the first-run setup endpoint, all required system and user services, and Gateway's `lifecycle.coreReady` health signal before reporting success. Progress includes the active image preparation or connector bootstrap phase so users can see what is taking time. Connector discovery continues in the background after Gateway's API starts, so a slow first-use connector probe does not prevent the setup page from opening. Repeated unchanged status is shown every thirty seconds. A timeout leaves the installation in place for diagnosis and prints the relevant `systemctl` and journal commands; rerunning the installer after correcting the reported service issue is safe.

For Linux uninstall, data is preserved by default. `--delete-data` explicitly selects deletion but still requires typing `UNINSTALL` in the interactive confirmation. `--yes` skips prompts and confirms deletion of databases and workspace files as well as uninstalling services and binaries; use it only when all Kautuka data should be removed.

When first-run setup is required, the Linux installer displays the one-time token only in the controlling terminal and waits for the user to type `COPIED`. It explains that the token is needed to create the first administrator in Kautuka's setup page. If the terminal was closed, retrieve it with `sudo cat /var/lib/kautuka/setup-token`. The file remains owner-only; the token is deleted after successful use. Never include it in logs or support reports.

Place `/var/lib/kautuka` on LUKS/dm-crypt or equivalently encrypted managed storage when file encryption at rest is required. Encrypt backups and snapshots separately; filesystem permissions alone are not encryption.

Do not document generated installer flags independently of the script's argument parser. Inspect the generated asset or run it with `--help` for the current interface.

## Trust warning

Network installs require a signed platform-release manifest and artifact SHA-256. Official Windows installers embed the trusted public key and release manifest URL at publish time (`InstallerManifestPublicKeyPath` / `InstallerReleaseManifestUrl`). Override with `ManifestPublicKeyPath` / `KAUTUKA_MANIFEST_PUBLIC_KEY_PATH` when needed. Local `RootfsPath` installs without a remote manifest remain allowed. `--allow-unsigned-local-feeds` is Development/source-checkout only.

The appliance deployment section of a signed platform manifest declares the hook ABI and digest-pinned images for CredentialsHost, ChannelHost, Gateway, WebHost, and UpdateService. This section accepts only the fixed service roles and their image references. The release signing tool accepts it with `--appliance-deployment`. UpdateService requires a verified signature for manifests that include this section and refuses to prepare them through the existing host-service activation path.

For a signed Linux install, the installer preserves the exact verified manifest and detached signature in Manager's version directory before switching the `current` link. For an appliance release, it also checks that all five role archives are regular files from the verified platform archive and installs them under the same version's `service-images/` directory. The metadata and archives are owned by root and readable by the Manager service account.

The release builder can produce an appliance candidate with `--appliance-image-prefix` and `--appliance-runtime-base`. It packages the same staged Native AOT service payloads, including Gateway's connector bundles and WebHost's built UI. The Linux platform archive includes the five OCI archives under `service-images/`, and the platform manifest signs each archived image's manifest digest. Normal service startup and updates need Manager's appliance activation path before they can use these images.

On Linux, Manager's `--verify-appliance-release` command verifies the selected release against the installed public key. It reads the manifest and signature from the root-owned version directory selected by `/opt/kautuka/manager/current`. A successful check verifies the signed image references and required hook ABI. The command exits before starting Manager or any application service.

When the Linux installer receives a signed appliance payload, it stages it under `/opt/kautuka/manager/versions` and runs the staged Manager's image preparation command as the service account before stopping services or changing `current`. The release directories stay root-owned and allow the service account to traverse them even when the installer runs with a restrictive umask. The installer makes the public release identity readable by Manager, which checks it against the signed manifest. The selected version directory stays in use during preparation, so the candidate needs a different version.

The signed appliance payload must include the Native AOT OCI hook. The installer checks the staged hook before stopping services and registers `/opt/kautuka/manager/current/scripts/kautuka-oci-hook` for Podman's `createRuntime` stage.

Manager's `--prepare-appliance-images` command performs the same signature check and prepares the five images in the calling service account's Podman store. It loads missing images from the protected role archives and checks each image's repository digest, service role, and platform. This command completes before application startup.

Both commands accept `--candidate-version <version>` to inspect a staged release under Manager's protected versions directory while the active release continues running. When all five images are already installed, preparation validates them with one Podman inspection and requires no duplicate OCI archives. Manager uses the image IDs from that inspection when it starts the application containers. Missing images still require their signed role archives. These commands leave the active release pointer in place.

In appliance mode, Manager's existing `GetHostStatus` RPC reports `core_ready` once the Podman runtime is available, the egress startup gate is open, and the supervised application services pass their local health checks. Manager also checks that the observability container is running.

When the egress inventory detects that the package proxy stopped, Manager requests a workspace sidecar reconciliation. Package requests stay blocked until the proxy is ready again.

The application containers use an explicit temporary home directory for .NET certificate validation across starts.

On a fresh signed Linux appliance installation, the installer provisions private socket directories and channel trust, then enables and starts Manager as the only Kautuka application host unit. Manager starts with the service account's systemd user target on subsequent boots. Manager creates missing application state directories with owner-only access and starts the application containers. The channel CA signing key stays in the root-only offline authority directory. The installer waits for the appliance's core readiness through WebHost and reads the first-run setup token from the Gateway state directory.

The signed appliance installer currently requires a fresh state root. Existing installations need a managed migration and activation transaction to preserve their data.

Linux application processes can run with `KAUTUKA_APPLIANCE_VERIFY=1` during candidate verification. This holds the existing admission and worker fences for the lifetime of the process while leaving health checks available. Gateway's readiness check can still call CredentialsHost's authenticated health RPC. The activation path still needs to set this environment value when launching the candidate.

The recovery helper accepts `--layout appliance` for capture, verification, and restore. It records the layout in the snapshot manifest and checks it before restoring. The appliance layout locates Gateway's databases, keys, and per-agent databases under `gateway/` within the state root. Existing snapshots without a layout field are read as host-service snapshots. During maintenance, Manager lists and stops its managed service containers by ID, including containers left running after a Manager restart. Manager and the containerized database writers share the root-owned `/run/kautuka-db-writer.lock`. On SELinux hosts, the installer registers its shared file context for boot and Manager mounts it read-only. The appliance activation path still needs to invoke recovery under the exclusive database writer lease.

The opt-in `Kautuka:Appliance:Enabled` startup setting enables initial application supervision on Linux in production mode. It verifies the selected signed release, prepares the images, and reconciles CredentialsHost and ChannelHost against that release with separate state and Unix socket mounts. ChannelHost receives its own server key, public Gateway trust material, and its own Manager client credential. CredentialsHost allows outbound TCP only to the Envoy telemetry listener. The installers currently leave this setting disabled.

ChannelHost also resolves provider names through CoreDNS and establishes provider TLS through Envoy. Manager registers the ChannelHost identity after verifying its native namespace rules. The network policy permits port 443 for `api.telegram.org`, `slack.com`, and Slack subdomains. This includes TLS for Slack Socket Mode. The authorization gate, system brake, explicit blocks, and Envoy's public destination filtering apply to these connections.

Gateway's model and remote MCP clients use caller-scoped Envoy CONNECT when started with `Kautuka:Appliance:Enabled`. Gateway requires the Envoy CA certificate and its own client credential at `/run/kautuka-gateway-connect/ca.crt` and `/run/kautuka-gateway-connect/client.pfx` at startup. Model requests require a caller identity. Remote MCP connections carry the authorized user, agent, and connector. Connectors with public-only destinations use the Envoy public route even when a network rule would allow LAN access.

After verifying the appliance release, Manager generates a dedicated CONNECT identity on the installed appliance and validates it on later starts. The Gateway and Envoy each have an owner-only host directory with their own leaf private key and a public CA certificate. The CA private key exists only during first-time generation. Manager mounts only the listener's certificate files into Envoy and admits CONNECT from the reserved Gateway address.

Manager's appliance supervisor starts Gateway once CredentialsHost, ChannelHost, and the egress boundary are ready. It checks the release's native hook registration, authorizes the exact Gateway container and image IDs, then verifies the container's network attachment and nftables rules. Gateway serves its local readiness probe over a Unix socket. Its peer mounts expose individual client certificates, channel trust material, and peer sockets. Workspace and MCP bundle directories retain their host paths so Manager can resolve paths Gateway publishes. Manager recreates Gateway when a peer's file-mounted credential changes. Existing host-service data requires migration before activation. Automatic CONNECT certificate renewal and installer activation are pending.

If Podman assigns the Gateway address after the `createRuntime` hook, the hook can install the restricted rules in the pinned namespace for the pre-authorized Gateway before the interface appears. Manager still verifies the actual address and rules before accepting readiness.

Appliance socket directories are distinct from the state root so a peer socket mount cannot include the peer's private key directory.

Manager publishes WebHost only at `127.0.0.1:20229` in appliance mode. WebHost starts after Gateway and mounts Gateway's local socket read-only. Its namespace allows incoming IPv4 HTTP, replies to established connections, and outgoing TCP to the Envoy telemetry listener. It drops other new outbound connections and IPv6.

Manager also supervises the signed UpdateService image. UpdateService receives the selected release identity from Manager and queries Manager over its authenticated Unix socket for runtime status. Gateway mounts the UpdateService socket and its client trust material after UpdateService passes its authenticated health check. UpdateService can reach the Envoy telemetry listener. In appliance mode, release discovery, in-app downloads, and automatic refresh of the hosted connector and skill catalog are disabled. The host recovery helper handles activation and recovery for signed bundles supplied locally.

The Linux appliance service integration test starts the production Envoy and CoreDNS topology in an isolated rootless Podman store. The native hook and startup canary must open the gate before the test starts Gateway and verifies the published WebHost route. The fixture uses locally cached, digest-pinned egress images through a read-only additional image store.

The appliance telemetry listener accepts HTTP OTLP POST requests for logs, metrics, and traces at Envoy's internal address, `10.247.0.2:15005`. Gateway, WebHost, CredentialsHost, and ChannelHost export to this endpoint. The boundary rules allow Envoy to reach OpenObserve at `10.247.0.6:5080`. The listener returns 404 for other methods and paths, including OpenObserve's query and administration APIs.

The native hook has an appliance OpenObserve profile for `10.247.0.6`. It requires a Manager-authorized container ID and image ID, accepts requests on port 5080 from Envoy, and permits replies on established connections. It drops new outbound connections and IPv6. Manager uses the same profile to verify the live ruleset after startup.

In appliance mode, Manager starts the pinned OpenObserve image after the egress boundary is ready. The collector stores its data under the appliance state root and receives its bootstrap password through a persistent Podman secret. Envoy attaches the authorization header on the ingestion route using its private rendered configuration. Manager requires the existing secret when the collector's data directory is populated.

For an already running package proxy, Manager reads Podman's scheduled health result and reuses the resolved image ID for identity verification. It still checks the live namespace's egress rules before marking the proxy ready.

In appliance mode, the package proxy wrapper builds from the locally imported, digest-pinned base image with networking and automatic pulls disabled. The build retains the scheduled Podman health check. Once running, the proxy fetches upstream packages through Envoy.

Once the appliance services are ready, Manager inspects their containers in one Podman call per reconciliation. It keeps checking individual service health and live egress rules. If the batch fails, Manager inspects services individually to recover stopped or missing containers.

Manager can start a selected signed release for pre-commit verification when the protected recovery marker names that release as the verification target. The signed application images must declare support for the update fence. Candidate containers keep ordinary requests and background work paused for their process lifetime while the supervisor brings up their health endpoints and the egress boundary. The update transaction must restart the cohort to resume ordinary operation.

Manager's authenticated host status reports its selected signed release ID and whether it booted in verification mode. Appliance UpdateService uses that status only when both values match its own release and mode.

Candidate readiness also requires a healthy package proxy with verified egress rules. Manager reconciles the proxy during the fenced boot without starting the workspace supervisor.

During a fenced boot, Manager sends empty OTLP requests for logs, metrics, and traces from WebHost's network namespace through the existing Envoy ingestion listener. Candidate readiness requires all three requests to succeed.

On the Linux appliance, `kautuka-manager --check-appliance-ready` checks the selected signed release over Manager's authenticated Unix socket. Pass `--candidate-version VERSION` to expect a specific installed release. With `KAUTUKA_APPLIANCE_VERIFY=1`, the check also requires the protected recovery marker to name that verification target.

Image preparation reuses installed digests for a staged release and requires a local archive for any missing digest. A candidate that changes an image without supplying its archive fails preparation and identifies the missing service.

The existing host recovery helper accepts `--stage-appliance --artifact PATH --version VERSION` for an installed Manager-supervised appliance. It verifies the supplied signed bundle, stages the protected version directory, and preflights the candidate's images in the service account's Podman store. Staging runs with the active services online.

The same helper accepts `--activate-appliance --artifact PATH --version VERSION`. It stages the bundle, drains Manager and the application containers, and takes a verified snapshot under the exclusive writer lease. It then switches the Manager release and boots the candidate in verification mode. A failed verification restores the source snapshot and release. Once the helper records the commit, a restart failure is recovered by starting the committed release again. The early-boot recovery unit uses the durable journal to select the correct release before the user service starts.

Standalone configuration updates are deferred. Appliance configuration changes ship in the signed release bundle. A release can retain the same pinned service image digests and reuse those images from the local Podman store. Keep the bundle workflow until a specific configuration change needs to ship independently.
