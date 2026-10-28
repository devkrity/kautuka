# Installation

## Development

Use [Development workflow](../development/workflow.md) for a source checkout. Development state is user-scoped and separate from production appliance state.

## Windows appliance

`Kautuka.Installer.exe` is the supported Windows entry point. It checks/enables WSL prerequisites, preserves reboot-resume state, resolves a release, imports the appliance into the selected Windows directory, starts the appliance, and waits for `http://localhost:20229` before reporting success.

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

The source-checkout convenience bootstrap is Development-only. It may create a shared local certificate layout for a single developer account, but production system units reject that fallback and require the four installer-owned inventories above. Do not copy development certificate directories into an appliance or set a production service to `Development` to bypass preflight.

Place `/var/lib/kautuka` on LUKS/dm-crypt or equivalently encrypted managed storage when file encryption at rest is required. Encrypt backups and snapshots separately; filesystem permissions alone are not encryption.

Do not document generated installer flags independently of the script's argument parser. Inspect the generated asset or run it with `--help` for the current interface.

## Trust warning

Network installs require a signed platform-release manifest and artifact SHA-256. Official Windows installers embed the trusted public key and release manifest URL at publish time (`InstallerManifestPublicKeyPath` / `InstallerReleaseManifestUrl`). Override with `ManifestPublicKeyPath` / `KAUTUKA_MANIFEST_PUBLIC_KEY_PATH` when needed. Local `RootfsPath` installs without a remote manifest remain allowed. `--allow-unsigned-local-feeds` is Development/source-checkout only.
