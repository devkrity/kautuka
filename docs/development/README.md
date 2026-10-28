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

The script reads expected versions from the repository, checks or installs missing prerequisites, clones missing sibling repositories, restores dependencies, and builds the solution. When Podman does not already have `localhost/kautuka-base:latest`, setup builds and verifies the local sandbox image used by Manager. This step downloads its Fedora base image and the checksummed Node and uv archives. Setup copies local configuration from checked-in examples only when the destination file does not exist. Review each generated local configuration file and supply only the credentials required for the integrations you intend to use; setup does not retrieve or display secrets.

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

The launcher builds required native services and starts the local stack. The WebHost is available at `http://localhost:20229`. Use **Launch Gateway** when you need to debug Gateway alone. Stop the launched stack with Ctrl+C in the terminal that owns it.

## Validation commands

Run the checks relevant to the files and behavior changed:

| Area | Command |
|---|---|
| Backend build and tests | `dotnet test src/Kautuka.slnx` |
| .NET formatting | `dotnet format src/Kautuka.slnx --verify-no-changes` |
| Frontend unit tests | `pnpm --dir src/frontend/web test` |
| Frontend browser tests | `pnpm --dir src/frontend/web test:e2e` |
| Frontend lint | `pnpm --dir src/frontend/web lint` |
| Frontend build | `pnpm --dir src/frontend/web build` |
| Documentation links and Narad references | `npm run docs:check` |

Tests that require external providers should use documented test doubles or local test services. Do not put credentials or production data in test fixtures.

## Hooks and commits

Setup enables the repository's Husky.Net hooks. Keep them enabled: pre-commit checks include documentation validation and secret scanning. Sign every commit with `git commit -s`; the sign-off records acceptance of the Developer Certificate of Origin. See [Contributing](../../CONTRIBUTING.md) for compatibility requirements and the pull request checklist.

## Architecture and security

Read the [system architecture](../architecture/system.md) for service responsibilities and dependency direction, the [execution model](../architecture/execution.md) for durable runs and approvals, and the [security model](../architecture/security.md) for trust boundaries and current limitations. In general, Gateway owns application authority; CredentialsHost, Manager, and ChannelHost own narrower responsibilities behind internal service contracts. Browser projections and message delivery do not replace canonical Gateway state.

## Troubleshooting

- **A version check fails:** use `--check` and confirm the expected value in the version source linked above. Correct PATH or activate the pinned runtime before retrying.
- **Podman is unavailable:** start the Podman machine/service for your platform and verify `podman info` before launching.
- **A port is already occupied:** identify the process using the runtime ports listed in the [runtime reference](../reference/runtime.md) and stop it if appropriate.
- **Dependency or build errors follow a branch change:** rerun setup to restore dependencies, then repeat the failing build or test command.
- **VS Code does not show the launch profile:** open the repository root, confirm `.vscode/launch.json` is present, and install recommended extensions if prompted.
- **A local provider integration fails:** verify the relevant example-derived settings and credentials locally. Redact secrets and tokens before sharing logs.
