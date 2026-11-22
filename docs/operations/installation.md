# Installation

## Development

Use [Development workflow](../development/workflow.md) for a source checkout. Development state is user-scoped and separate from production appliance state.

## Windows appliance

`Kautuka.Installer.exe` is the supported Windows entry point. It checks/enables WSL prerequisites, preserves reboot-resume state, resolves a release, imports the appliance into the selected Windows directory, starts the appliance, and waits for `http://localhost:20229` before reporting success.

After installation, the Windows installer displays the one-time setup token in its local window and waits for the user to copy it and acknowledge that it is needed to create the first administrator in Kautuka's setup page. The token is deleted after successful use. To recover it, reopen the installer and select **Open Kautuka**. If the window is unavailable, run `wsl.exe -d <distro-name> -u root -- cat /var/lib/kautuka/gateway/setup-token` in PowerShell, substituting the selected distro name.

When Windows needs a restart to enable WSL, the installer saves its progress in the protected installer state directory. The interactive installer offers **Resume** after sign-in. Express installs return Windows exit code `3010` when a restart is required and keep express mode for the next run.

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

If initial appliance installation is interrupted, retry with the same signed release and web port. The installer records those values in a root-owned checkpoint, reuses already-published channel trust material, and clears the checkpoint once readiness passes. Use the exact release named in the retry message if the Preview channel has advanced. A completed appliance uses the managed update path.

When first-run setup is required, the Linux installer displays the one-time token only in the controlling terminal and waits for the user to type `COPIED`. It explains that the token is needed to create the first administrator in Kautuka's setup page. If the terminal was closed, retrieve the appliance token with `sudo cat /var/lib/kautuka/gateway/setup-token`. Legacy host-service installs use `/var/lib/kautuka/setup-token`. The file remains owner-only and the token is deleted after successful use. Never include it in logs or support reports.

Place `/var/lib/kautuka` on LUKS/dm-crypt or equivalently encrypted managed storage when file encryption at rest is required. Encrypt backups and snapshots separately; filesystem permissions alone are not encryption.

Do not document generated installer flags independently of the script's argument parser. Inspect the generated asset or run it with `--help` for the current interface.

## Trust warning

Network installs require a signed platform-release manifest and artifact SHA-256. Official Windows installers embed the trusted public key and release manifest URL at publish time (`InstallerManifestPublicKeyPath` / `InstallerReleaseManifestUrl`). Override with `ManifestPublicKeyPath` / `KAUTUKA_MANIFEST_PUBLIC_KEY_PATH` when needed. Local `RootfsPath` installs without a remote manifest remain allowed. `--allow-unsigned-local-feeds` is Development/source-checkout only.

The appliance deployment section of a signed platform manifest declares the hook ABI and digest-pinned images for CredentialsHost, ChannelHost, Gateway, WebHost, and UpdateService. This section accepts only the fixed service roles and their image references. The release signing tool accepts it with `--appliance-deployment`. UpdateService requires a verified signature for manifests that include this section and refuses to prepare them through the existing host-service activation path.

For a signed Linux install, the installer preserves the exact verified manifest and detached signature in Manager's version directory before switching the `current` link. For an appliance release, it also checks that all five role archives are regular files from the verified platform archive and installs them under the same version's `service-images/` directory. The metadata and archives are owned by root and readable by the Manager service account.

The release builder can produce an appliance candidate with `--appliance-image-prefix` and `--appliance-runtime-base`. It packages the same staged Native AOT service payloads, including Gateway's connector bundles and WebHost's built UI. The Linux platform archive includes the five OCI archives under `service-images/`, and the platform manifest signs each archived image's manifest digest. Normal service startup and updates need Manager's appliance activation path before they can use these images.

Release discovery and preparation for an appliance require a signed manifest with an appliance deployment descriptor. A host-service release is ineligible even if it uses the same Linux artifact name. Preparation preserves the verified manifest and signature for independent verification by the host recovery helper.

UpdateService compares the newest published GitHub tag in the selected channel with the installed appliance version. It fetches the signed manifest only for a newer release.

On Linux, Manager's `--verify-appliance-release` command verifies the selected release against the installed public key. It reads the manifest and signature from the root-owned version directory selected by `/opt/kautuka/manager/current`. A successful check verifies the signed image references and required hook ABI. The command exits before starting Manager or any application service.

When the Linux installer receives a signed appliance payload, it stages it under `/opt/kautuka/manager/versions` and runs the staged Manager's image preparation command as the service account before stopping services or changing `current`. The release directories stay root-owned and allow the service account to traverse them even when the installer runs with a restrictive umask. The installer makes the public release identity readable by Manager, which checks it against the signed manifest. The selected version directory stays in use during preparation, so the candidate needs a different version.

The signed appliance payload must include the Native AOT OCI hook. The installer checks the staged hook before stopping services and registers `/opt/kautuka/manager/current/scripts/kautuka-oci-hook` for Podman's `createRuntime` stage.

Manager's `--prepare-appliance-images` command performs the same signature check and prepares the five images in the calling service account's Podman store. It loads missing images from the protected role archives and checks each image's repository digest, service role, and platform. This command completes before application startup.

Both commands accept `--candidate-version <version>` to inspect a staged release under Manager's protected versions directory while the active release continues running. When all five images are already installed, preparation validates them with one Podman inspection and requires no duplicate OCI archives. Manager uses the image IDs from that inspection when it starts the application containers. Missing images still require their signed role archives. These commands leave the active release pointer in place.

In appliance mode, Manager's existing `GetHostStatus` RPC reports `core_ready` once the Podman runtime is available, the egress startup gate is open, and the supervised application services pass their local health checks. Manager also checks that the observability container is running.

On first boot, Manager provisions the OpenObserve ingestion credential through the collector's private loopback interface. Envoy receives the named ingestion token for its fixed OTLP endpoint. Manager keeps the token in its owner-only telemetry state for subsequent boots. The collector's root credential stays with the collector and Manager's provisioning call.

When the egress inventory detects that the package proxy stopped, Manager requests a workspace sidecar reconciliation. Package requests stay blocked until the proxy is ready again.

The application containers use an explicit temporary home directory for .NET certificate validation across starts.

On a fresh signed Linux appliance installation, the installer provisions private socket directories and channel trust, then enables and starts Manager as the only Kautuka application host unit. Manager starts with the service account's systemd user target on subsequent boots. Manager creates missing application state directories with owner-only access and starts the application containers. The channel CA signing key stays in the root-only offline authority directory. The installer waits for the appliance's core readiness through WebHost and reads the first-run setup token from the Gateway state directory.

While Manager is running, reopen the usual local browser address if WebHost becomes unavailable. Manager can temporarily serve a read-only recovery message there after WebHost releases the port. The page shows a scheduled retry when one is pending or an action when recovery needs help. If a saved network pause prevents verification at startup, the page reports that pause and directs the owner to support. The workload admission gate stays closed during a network protection failure. Manager releases the port before WebHost restarts, so a refresh during the handoff can briefly fail.

The signed appliance installer currently requires a fresh state root. Existing installations need a managed migration and activation transaction to preserve their data.

Build a Linux-only signed preview with `scripts/build-release-assets.sh --linux-appliance-only`, the existing appliance image options, and the preview's exact artifact base URL. The output includes the Linux archive, signed manifest, and Linux installer. Verify this artifact set with `scripts/verify-release-artifacts.mjs --linux-appliance-only true` and the usual release identity and public key arguments.

Use a dedicated distribution path for these artifacts. The shared Windows Preview installer currently selects the newest eligible tag before checking for a Windows artifact.

On a fresh signed Linux appliance, pass `--web-port 23084` to publish WebHost on a different localhost port. Manager saves the port in its systemd user unit. The default is `20229`. The container listener and its egress rules stay the same.

The host recovery helper sets `KAUTUKA_APPLIANCE_VERIFY=1` when booting a candidate for verification. This holds the existing admission and worker fences for the lifetime of the process while leaving health checks available. Gateway's readiness check can still call CredentialsHost's authenticated health RPC.

The recovery helper accepts `--layout appliance` for capture, verification, and restore. It records the layout in the snapshot manifest and checks it before restoring. The appliance layout locates Gateway's databases, keys, and per-agent databases under `gateway/` within the state root. Existing snapshots without a layout field are read as host-service snapshots. During maintenance, Manager lists and stops its managed service containers by ID, including containers left running after a Manager restart. Manager and the containerized database writers share the root-owned `/run/kautuka-db-writer.lock`. On SELinux hosts, the installer registers its shared file context for boot and Manager mounts it read-only. The host helper takes the exclusive writer lease for snapshot capture and restoration.

The installer sets `Kautuka:Appliance:Enabled` when it installs a signed appliance release. Manager verifies the selected release, prepares the images, and reconciles CredentialsHost and ChannelHost with separate state and Unix socket mounts. ChannelHost receives its own server key, public Gateway trust material, and its own Manager client credential. CredentialsHost allows outbound TCP only to the Envoy telemetry listener.

ChannelHost resolves provider names through CoreDNS and establishes provider TLS through Envoy even when observability is disabled. Manager registers the ChannelHost identity after verifying its native namespace rules. The network policy permits port 443 for `api.telegram.org`, `slack.com`, and Slack subdomains. This includes TLS for Slack Socket Mode. The authorization gate, system brake, explicit blocks, and Envoy's public destination filtering apply to these connections.

ChannelHost checks the authorized destination of each open Slack socket during its existing supervision loop. A denied destination closes the socket. Reconnects still require authorization.

Gateway's HTTP clients use caller-scoped Envoy CONNECT when started with `Kautuka:Appliance:Enabled`. This includes model and remote MCP requests, social sign-in, connector OAuth, mail brokerage, and catalog downloads. Requests require an explicit caller identity and continue to check policy before dispatch. Gateway requires the Envoy CA certificate and its own client credential at `/run/kautuka-gateway-connect/ca.crt` and `/run/kautuka-gateway-connect/client.pfx` at startup. For an HTTPS origin, Gateway separately negotiates and validates provider TLS inside the tunnel. Connections are pooled by caller scope, with separate pools for temporary provider-test tokens. Connectors with public-only destinations use the Envoy public route even when a network rule would allow LAN access.

The system Gateway scope starts with HTTPS access to `auth.kautuka.io`, `www.googleapis.com`, and `login.microsoftonline.com` for social sign-in and signing-key discovery. An owner can block any of these destinations. Signed-in connector OAuth and mail brokerage keep their user and connector scope.

First run shows the service destinations from the appliance's seeded rules. The owner can review or change the defaults for sign-in, Slack, Telegram, and in-app updates before confirming and continuing. Advanced Settings uses the same rules.

When a remote MCP connection needs approval, the connection test directs the user to Network Access. A block without a pending request points to both rules and requests. DNS, TLS, and provider failures keep their own messages.

The appliance can hold one call per pending network request for up to ten minutes. It defaults to 64 held calls across the appliance and eight per user. Set `Kautuka__NetworkAccess__ApprovalHold__Duration`, `Kautuka__NetworkAccess__ApprovalHold__MaxHeldCalls`, and `Kautuka__NetworkAccess__ApprovalHold__MaxHeldCallsPerUser` in the Manager service environment to change these values. A call cancelled by its caller releases its slot. Remote MCP starts its discovery timeout after Manager authorizes the connection.

After verifying the appliance release, Manager generates a dedicated CONNECT identity and checks its lifetime on later starts. Manager replaces the certificate pair when it reaches the renewal window and starts Gateway and Envoy with the renewed identity. Gateway and Envoy each have an owner-only host directory with their own leaf private key and a public CA certificate. Manager mounts only the listener's certificate files into Envoy and admits CONNECT from the reserved Gateway address.

Manager uses local digest-pinned images for the appliance boundary, Envoy, and CoreDNS. It can restore a missing image from the verified release archive. If the image is still unavailable, the egress startup gate stays closed.

Manager's appliance supervisor starts Gateway once CredentialsHost, ChannelHost, and the egress boundary are ready. It checks the release's native hook registration, authorizes the exact Gateway container and image IDs, then verifies the container's network attachment and nftables rules. Gateway serves its local readiness probe over a Unix socket. Its peer mounts expose individual client certificates, channel trust material, and peer sockets. Workspace and MCP bundle directories retain their host paths so Manager can resolve paths Gateway publishes. Manager recreates Gateway when a peer's file-mounted credential changes. This release supports fresh appliance installations.

If Podman assigns the Gateway address after the `createRuntime` hook, the hook can install the restricted rules in the pinned namespace for the pre-authorized Gateway before the interface appears. Manager still verifies the actual address and rules before accepting readiness.

Appliance socket directories are distinct from the state root so a peer socket mount cannot include the peer's private key directory.

Manager publishes WebHost on IPv4 localhost in appliance mode. The default port is `20229`. WebHost starts after Gateway and mounts Gateway's local socket read-only. Its namespace allows incoming IPv4 HTTP, replies to established connections, and outgoing TCP to the Envoy telemetry listener. It drops other new outbound connections and IPv6.

Manager also supervises the signed UpdateService image. UpdateService receives the selected release identity and verification public key from Manager and queries Manager over its authenticated Unix socket for runtime status. Gateway mounts the UpdateService socket and its client trust material after UpdateService passes its authenticated health check. UpdateService can reach the Envoy telemetry listener. Its release network policy allows TLS on port 443 through CoreDNS and Envoy to `api.github.com`, `github.com`, and `release-assets.githubusercontent.com`. The in-app updater discovers and downloads signed appliance releases through that path. It checks the running release and active executions before download and again before requesting activation. Automatic refresh of the hosted connector and skill catalog remains disabled in appliance mode.

The Linux appliance service integration test starts the production Envoy and CoreDNS topology in an isolated rootless Podman store. The native hook and startup canary must open the gate before the test starts Gateway and verifies the published WebHost route. The fixture uses locally cached, digest-pinned egress images through a read-only additional image store.

The appliance telemetry listener accepts HTTP OTLP POST requests for logs, metrics, and traces at Envoy's internal address, `10.247.0.2:15005`. Gateway, WebHost, CredentialsHost, and ChannelHost export to this endpoint. The boundary rules allow Envoy to reach OpenObserve at `10.247.0.6:5080`. The listener returns 404 for other methods and paths, including OpenObserve's query and administration APIs.

The native hook has an appliance OpenObserve profile for `10.247.0.6`. It requires a Manager-authorized container ID and image ID, accepts requests on port 5080 from Envoy, and permits replies on established connections. It drops new outbound connections and IPv6. Manager uses the same profile to verify the live ruleset after startup.

In appliance mode, Manager starts the pinned OpenObserve image after the egress boundary is ready and disables OpenObserve's vendor telemetry. The collector stores its data under the appliance state root and reads its bootstrap password from a read-only configuration file mounted only in the collector. Manager requires the existing file when the collector's data directory is populated. For ingestion, Envoy reads the separate credential from a file inside Manager's private Envoy directory. The generated YAML references the file by path, and Envoy replaces any request authorization header on the OTLP listener.

At startup, Manager retries the explicit package proxy health probe within a bounded window. This works when the rootless Podman session has no scheduled health checks. During routine reconciliation, the same previously verified container can continue serving packages while Manager checks its health, image and live egress rules. Failure, a different container ID, or a detected need for replacement clears readiness. A replacement must pass verification before it can serve packages.

Manager debug logs report how long connector and sandbox artifact builds waited for the image build lock and how long workspace reconciliation took for each trigger.

A new connector's save-time discovery can continue after one concurrent catalog publication if the connector is still unpublished and the runtime settings are unchanged. Admission still verifies the exact revision used to start its container.

An MCPB probe gets a three-minute image-preparation budget in addition to the connector's discovery timeout. This accommodates a cold build waiting for another build to finish. Set `Kautuka__ConnectorImagePreparationSeconds` in the Manager service environment to change the preparation budget.

Workload reconciliation compares the Podman listing with the registry entries captured before that listing began. A package build keeps its pre-start registration until its own cleanup runs. The registry removes that build's registration even if reconciliation has updated its container ID. If an egress inspection times out during reconciliation, Manager closes launch admission and verifies the full boundary on the next pass. If that retry finds the pod unhealthy or still cannot verify its health, Manager recreates it before reopening admission.

On restart, Manager removes package builders that have no active registration. If removal fails, Manager quarantines the builder and retries during reconciliation.

In appliance mode, the package proxy wrapper builds from the locally imported, digest-pinned base image with networking and automatic pulls disabled. The build retains the scheduled Podman health check. Once running, the proxy fetches upstream packages through Envoy.

UpdateService keeps the Public Suffix List in appliance data and refreshes it through Envoy. The first-run service network settings include `publicsuffix.org` under in-app updates. The refresh defaults to weekly. Set `Update__PublicSuffixRefreshInterval`, `Update__PublicSuffixMaxRuleDropPercent`, and `Update__PublicSuffixMinimumRuleCount` in the Manager service environment to change the schedule, the default 25 percent rule-loss limit, and the default minimum of 5,000 rules. UpdateService replaces the list only after validating both sections and the rule count. Without a valid list, pending approvals and existing allows cover exact hosts, and existing domain blocks still protect their subdomains.

Once the appliance services are ready, Manager inspects their containers in one Podman call per reconciliation. It keeps checking individual service health and live egress rules. If the batch fails, Manager inspects services individually to recover stopped or missing containers.

Manager can start a selected signed release for pre-commit verification when the protected recovery marker names that release as the verification target. The signed application images must declare support for the update fence. Candidate containers keep ordinary requests and background work paused for their process lifetime while the supervisor brings up their health endpoints and the egress boundary. The update transaction must restart the cohort to resume ordinary operation.

If backup pruning fails after activation commits, the update helper reports a failure and leaves the active release and retained recovery point in place. Recovery can retry pruning without restoring older data.

Manager's authenticated host status reports its selected signed release ID and whether it booted in verification mode. Appliance UpdateService uses that status only when both values match its own release and mode.

Candidate readiness also requires a healthy package proxy with verified egress rules. Manager reconciles the proxy during the fenced boot without starting the workspace supervisor.

During a fenced boot, Manager sends empty OTLP requests for logs, metrics, and traces from WebHost's network namespace through the existing Envoy ingestion listener. Candidate readiness requires all three requests to succeed.

On the Linux appliance, `kautuka-manager --check-appliance-ready` checks the selected signed release over Manager's authenticated Unix socket. Pass `--candidate-version VERSION` to expect a specific installed release. With `KAUTUKA_APPLIANCE_VERIFY=1`, the check also requires the protected recovery marker to name that verification target.

Run `scripts/installed-linux-smoke.sh --appliance-version VERSION --base-url http://127.0.0.1:20229` on the installed host. Use the configured localhost port if it differs. The smoke test checks the exact signed release, the activation watcher, WebHost's published port, and authenticated execution readiness.

Image preparation reuses installed digests for a staged release and requires a local archive for any missing digest. A candidate that changes an image without supplying its archive fails preparation and identifies the missing service.

The existing host recovery helper accepts `--stage-appliance --artifact PATH --version VERSION` for an installed Manager-supervised appliance. It verifies the supplied signed bundle, stages the protected version directory, and preflights the candidate's images in the service account's Podman store. Staging runs with the active services online.

The same helper accepts `--activate-appliance --artifact PATH --version VERSION`. It stages the bundle, drains Manager and the application containers, and takes a verified snapshot under the exclusive writer lease. It then switches the Manager release and boots the candidate in verification mode. A failed verification restores the source snapshot and release. Once the helper records the commit, a restart failure is recovered by starting the committed release again. The early-boot recovery unit uses the durable journal to select the correct release before the user service starts.

Fresh appliance installs enable a systemd path unit that watches UpdateService's private updates directory for an activation request. UpdateService's appliance handoff accepts the canonical staged Linux archive and publishes the version file with an atomic rename. It preserves an existing request. The host helper consumes the request under the release lock, accepts only a version, and derives the archive path itself. It uses the same signed-bundle verification and recovery transaction as a locally staged update.

The systemd path is the appliance's privileged update entry point. A signed Linux appliance install removes the legacy sudo grant for the service account to invoke the recovery helper directly.

UpdateService reads the host recovery status through Manager's authenticated socket. A candidate's fenced verification boot cannot complete an update in the app. Rollback status requires a healthy source release and a recovery journal entry for the current attempt.

Standalone configuration updates are deferred. Appliance configuration changes ship in the signed release bundle. A release can retain the same pinned service image digests and reuse those images from the local Podman store. Keep the bundle workflow until a specific configuration change needs to ship independently.
