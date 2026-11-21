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

Manager's `--prepare-appliance-images` command performs the same signature check and prepares the five images in the calling service account's Podman store. It loads missing images from the protected role archives and checks each image's repository digest, service role, and platform. This command completes before application startup.

The opt-in `Kautuka:Appliance:Enabled` startup setting enables initial application supervision on Linux in production mode. It verifies the selected signed release, prepares the images, and reconciles CredentialsHost and ChannelHost against that release with separate state and Unix socket mounts. ChannelHost receives its own server key, public Gateway trust material, and its own Manager client credential. Both containers use network isolation during staged integration. The installers currently leave this setting disabled.

Gateway's model and remote MCP clients use caller-scoped Envoy CONNECT when started with `Kautuka:Appliance:Enabled`. Gateway requires the Envoy CA certificate and its own client credential at `/run/kautuka-gateway-connect/ca.crt` and `/run/kautuka-gateway-connect/client.pfx` at startup. Model requests require a caller identity. Remote MCP connections carry the authorized user, agent, and connector. Connectors with public-only destinations use the Envoy public route even when a network rule would allow LAN access.

After verifying the appliance release, Manager generates a dedicated CONNECT identity on the installed appliance and validates it on later starts. The Gateway and Envoy each have an owner-only host directory with their own leaf private key and a public CA certificate. The CA private key exists only during first-time generation. Manager mounts only the listener's certificate files into Envoy and admits CONNECT from the reserved Gateway address.

Manager's appliance supervisor starts Gateway once CredentialsHost, ChannelHost, and the egress boundary are ready. It checks the release's native hook registration, authorizes the exact Gateway container and image IDs, then verifies the container's network attachment and nftables rules. Gateway serves its local readiness probe over a Unix socket. Its peer mounts expose individual client certificates, channel trust material, and peer sockets. Workspace and MCP bundle directories retain their host paths so Manager can resolve paths Gateway publishes. Manager recreates Gateway when a peer's file-mounted credential changes. Existing host-service data requires migration before activation. Automatic CONNECT certificate renewal and installer activation are pending.

If Podman assigns the Gateway address after the `createRuntime` hook, the hook can install the restricted rules in the pinned namespace for the pre-authorized Gateway before the interface appears. Manager still verifies the actual address and rules before accepting readiness.

Appliance socket directories are distinct from the state root so a peer socket mount cannot include the peer's private key directory.
