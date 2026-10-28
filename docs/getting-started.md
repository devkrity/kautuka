# Getting started with Kautuka

This guide is for administrators installing and configuring Kautuka. For source builds and local development, see the [developer guide](development/README.md).

## System requirements

- **Windows:** Windows 10 or later with WSL 2 available.
- **Linux:** a distribution supported by the installer for the release you are installing.
- **Memory:** allocate approximately 2 GB to Kautuka. Additional capacity may be required by the chosen model provider and concurrent workloads.
- **Storage:** allow space for Kautuka, model data, documents, and session history. The project does not currently publish a single minimum free-disk requirement; consult release notes for image size.
- **Agent execution:** a working Podman runtime is required for chat workloads that invoke tools managed by Kautuka.
- **Access:** use a modern browser on the computer running Kautuka or another device that can reach its configured WebHost address. Remote access must be configured with TLS as described in the security and operations documentation.

## Install

1. Open [Kautuka releases](https://github.com/devkrity/kautuka/releases) and download the installer for your operating system. If no release is listed, a public installer is not yet available.
2. Run the installer and follow its prompts. On Windows, Kautuka uses WSL 2; ensure it is installed and available before starting Kautuka.
3. Start Kautuka and open the address it reports.

## Initial configuration

On a new installation, complete the first-run flow:

1. Create the initial administrator account. This account manages Kautuka configuration and access.
2. Continue through onboarding or choose manual configuration.
3. Configure an AI provider and save a model supported by that provider. The onboarding presets include Gemini, Ollama, and OpenRouter; the manual provider catalog may include additional options.
4. Create an agent and assign the configured model to it.
5. Open the agent workspace, start a session, and send a test prompt. A configured provider and model must be assigned to an agent before the agent can use them.
6. Configure tools, connectors, documents, skills, workflows, channels, or schedules as needed. Review the permissions and approval behavior for each capability before enabling it.

If the administrator has already been created, sign in with the existing account. Do not repeat the first-run flow to recover access; follow the account recovery guidance for your deployment.

## Approvals and operational review

Tools can be configured under different approval policies. When an action is configured to ask for approval, Kautuka records a request and pauses the relevant run until an authorized user resolves it or the configured expiry policy applies. Some workflow steps can also pause for review after completing a step. Review the request details and changed outputs before approving or requesting changes. See the [execution model](architecture/execution.md) and [security model](architecture/security.md) for the relevant behavior and limits.

The Dashboard provides Kautuka health and recent activity. Agent workspaces contain sessions and execution history. The Approvals page shows pending requests and decision history. Settings contains administrator-managed users, providers, models, and organization configuration.

## Troubleshooting

- **The web interface does not open:** confirm Kautuka is running and use the address and port it reports. For local development, verify that WebHost is listening on port `20229`.
- **An agent cannot start a chat:** check Kautuka health and verify that Podman is running where tool execution requires it.
- **No model is available to an agent:** configure a provider, save a model, then assign the model to the agent.
- **A provider request fails:** check the provider configuration, model identifier, network availability, and provider account status. Do not paste API keys into issue reports.
- **A first-run route reports that setup is complete:** sign in with the existing administrator account.
- **An approval has expired or the run remains paused:** inspect the approval history and the configured expiry action. See the [operations troubleshooting guide](operations/troubleshooting.md) for diagnostics.

When reporting a problem, include the Kautuka version, operating system, reproduction steps, and relevant redacted logs. Remove passwords, access tokens, OAuth credentials, private documents, and personal data before sharing diagnostics.

## Further documentation

- [Operations and troubleshooting](operations/troubleshooting.md)
- [Execution model](architecture/execution.md)
- [System architecture](architecture/system.md)
- [Security model](architecture/security.md)
