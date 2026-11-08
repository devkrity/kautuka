# Development guide

This guide describes how to prepare a local checkout, run Kautuka, and validate a change. It is intended for contributors working on the application and its companion repositories.

## Repository structure

| Path | Responsibility |
|---|---|
| `src/backend/` | .NET hosts and shared services, including Gateway, WebHost, Manager, CredentialsHost, ChannelHost, and UpdateService |
| `src/frontend/web/` | React application and browser tests |
| `src/frontend/narad/` | Narad Protocol workspace packages used by the application |
| `mcps/` | Standalone tool implementations, including Python-based retrieval tools |
| `src/tests/` and `tests/` | Backend contract, unit, integration, and acceptance tests |
| `docs/architecture/` | System, execution, data, security, and frontend ownership documentation |

Kautuka, [kautuka-auth](https://github.com/devkrity/kautuka-auth), and [Narad Protocol](https://github.com/devkrity/narad) are separate repositories. The world setup script places the sibling checkouts beside Kautuka by default.

## Prerequisites and supported environments

The committed version files and package metadata are authoritative:

- .NET SDK: [`global.json`](../../global.json)
- Node.js: [`.nvmrc`](../../.nvmrc) and [`package.json`](../../package.json)
- pnpm: the `packageManager` field in [`package.json`](../../package.json), activated through Corepack
- Python: [`.python-version`](../../.python-version), managed through uv

When a tool is missing, setup installs Node.js under `$HOME/.local/share/kautuka/node`, .NET under `$HOME/.dotnet`, and uv under `$HOME/.local/bin`. It adds one idempotent PATH line to `~/.profile` and `~/.bashrc` so these user-local tools are available in future shells.

Git, uv, and Podman are also required; Podman is used by Manager for selected tool workloads. VS Code is optional. Recommended extensions are listed in [`.vscode/extensions.json`](../../.vscode/extensions.json). The setup script supports Windows PowerShell, macOS bash, and Linux bash; package installation options depend on the operating system. Windows development should use a working WSL 2 and Podman configuration for appliance workloads.

## Initial setup

Run from the repository root:

```bash
./setup_the_world.sh
```

On Windows PowerShell:

```powershell
./setup_the_world.ps1
```

The script reads expected versions from the repository, checks or installs missing prerequisites, clones missing sibling repositories, restores dependencies, and builds the solution. Manager uses the immutable `kautuka-base` digest in its runtime image inventory and pulls that GHCR image on first startup when it is not already present. The base image has a separate release cycle; ordinary development setup does not rebuild it from Fedora sources. Setup copies local configuration from checked-in examples only when the destination file does not exist. Review each generated local configuration file and supply only the credentials required for the integrations you intend to use; setup does not retrieve or display secrets.

Available options:

| Option | Effect |
|---|---|
| `--check` | Report expected and detected tool versions without changing the machine or checkout. |
| `--repos kautuka-auth,narad` | Select which sibling repositories to fetch or clone. |
| `--skip-install` | Check prerequisites and perform setup without installing tools. |
| `--reset` | List and remove known build outputs, dependency directories, caches, and local development data, then run setup again. Prompts before deletion. |
| `--reset --yes` | Perform the same reset without the confirmation prompt. Review the listed targets before using this option. |
| `--help` | Display command usage. |

The reset operation is limited to known generated and local runtime paths. It does not remove source files or sibling repository work. On Linux, the script asks before invoking package-manager commands that require `sudo`.

## Run and debug

Open the repository root in VS Code and select **Launch Kautuka**, then press **F5**. The equivalent command is:

```bash
npm run launch
```

On Windows the launcher creates a dedicated **Fedora 44 WSL distro named
`KautukaDev-<checkout-id>`** on first use. It builds every Kautuka service and
.NET connector with **Native AOT**,
starts Manager and UpdateService inside Linux, and lets Manager start the core
containers, private user-agent networks and Envoy/CoreDNS boundary. Network
control is enabled. The first run downloads the distro, prerequisites and pinned
build/runtime images; later runs reuse them. Application data lives inside this
development distro, separately from Windows host-development data and installed
or acceptance-test appliances. Existing unmarked distros are never adopted.
The new distro receives a 64 GB virtual disk limit; physical disk usage grows
with downloaded images and development data. Building requires at least 12 GiB
free inside its Linux filesystem.

WebHost is available at `http://localhost:20229`. Stop the previous host-process
stack before starting this one, or select a different port:

```powershell
npm run launch -- --port 20239
```

The initial administrator setup token is kept inside the development distro. Read
it in your own terminal when the setup page requests it:

```powershell
wsl -d <distro-name-printed-by-launcher> -u root -- cat /var/lib/kautuka/gateway-data/setup-token
```

Use `npm run launch -- --skip-build` to start the last successful development
build without rebuilding assets. Normal launch rebuilds when source or assets
change. Data, configured providers, network policy, approvals and workspaces are
retained. These are local Native AOT development images, not signed release
artifacts; automatic release updates are disabled for this profile. Development
and release use the same AOT compilation path and core runtime base. There is no
JIT or `dotnet run` fallback.

Ctrl+C stops only this development stack and preserves its data. You can also
stop it from another terminal with `npm run launch -- --stop`. Use
`--distro KautukaDev-<name>` for another isolated checkout. The launcher does not
unregister distros, prune storage or reset application data.

### Operate on one service

Leave the main launcher running and use a second terminal:

```powershell
# Restart the existing WebHost container without rebuilding.
npm run launch -- --service webhost

# Build frontend assets and replace only WebHost's image; retain its native executable.
npm run launch -- --service frontend --action rebuild

# Publish Gateway with Native AOT, build its image and replace only Gateway.
npm run launch -- --service gateway --action rebuild

# Recover a stopped core service in an otherwise running stack.
npm run launch -- --service channelhost --action start
```

The fixed service names are `frontend` (WebHost assets), `webhost`, `gateway`,
`channelhost`, `credentialshost`, `manager`, and `updateservice`. The default
action is `restart`; `start` reuses the installed build, while `rebuild` builds
only the selected core service. Manager and UpdateService support `start` and
`restart`; use a normal full launch to rebuild these host services. Pass the
same `--distro` when using a named development instance. Service commands use
the instance's existing port and do not start a second foreground launcher.

Core service operations briefly restart Manager so it can load the updated
image inventory and use its normal reconciliation and network admission path.
The other core containers and Envoy/DNS stay running. Active tool calls may be
interrupted by the Manager restart. Application data is retained. Core service
commands require an initialized running stack; start the full stack normally
first if it is stopped. Shared-code changes may require rebuilding more than
one service; a normal full launch remains available for those changes.

The isolated container launcher currently targets Windows WSL; it must not run
the appliance installer on an arbitrary existing Linux desktop. For Linux
testing use the Native AOT appliance deployment in a dedicated environment.
The old six-host-process launcher and managed Gateway-only debug configuration
are removed so they cannot silently exercise a different runtime.

## Validation commands

Run the checks relevant to the files and behavior changed:

| Area | Command |
|---|---|
| Backend build and tests | `dotnet test src/Kautuka.slnx` |
| .NET whitespace formatting | `dotnet format whitespace src/Kautuka.slnx --verify-no-changes` |
| Frontend unit tests | `pnpm --dir src/frontend/web test` |
| Frontend browser tests | `pnpm --dir src/frontend/web test:e2e` |
| Frontend lint | `pnpm --dir src/frontend/web lint` |
| Frontend build | `pnpm --dir src/frontend/web build` |
| Documentation links and Narad references | `npm run docs:check` |

Tests that require external providers should use documented test doubles or local test services. Do not put credentials or production data in test fixtures.

## Hooks and commits

Setup installs the repository's pinned Lefthook hooks. Pre-commit runs documentation checks, staged-content secret scanning, and formatting/linting for staged C# and frontend files. Pre-push runs the full local .NET format, build, and test suite together with frontend lint, tests, and build. This keeps commits quick while preserving the complete local gate before pushing. Keep both hooks enabled. Sign every commit with `git commit -s`; the sign-off records acceptance of the Developer Certificate of Origin. See [Contributing](../../CONTRIBUTING.md) for compatibility requirements and the pull request checklist.

## Architecture and security

Read the [system architecture](../architecture/system.md) for service responsibilities and dependency direction, the [execution model](../architecture/execution.md) for durable runs and approvals, and the [security model](../architecture/security.md) for trust boundaries and current limitations. In general, Gateway owns application authority; CredentialsHost, Manager, and ChannelHost own narrower responsibilities behind internal service contracts. Browser projections and message delivery do not replace canonical Gateway state.

## Troubleshooting

- **A version check fails:** use `--check` and confirm the expected value in the version source linked above. Correct PATH or activate the pinned runtime before retrying.
- **Podman is unavailable:** start the Podman machine/service for your platform and verify `podman info` before launching.
- **A port is already occupied:** identify the process using the runtime ports listed in the [runtime reference](../reference/runtime.md) and stop it if appropriate.
- **Dependency or build errors follow a branch change:** rerun setup to restore dependencies, then repeat the failing build or test command.
- **VS Code does not show the launch profile:** open the repository root, confirm `.vscode/launch.json` is present, and install recommended extensions if prompted.
- **A local provider integration fails:** verify the relevant example-derived settings and credentials locally. Redact secrets and tokens before sharing logs.
