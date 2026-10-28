using Kautuka.Tui.Common;
using Kautuka.Tui.Features.Advanced;
using Kautuka.Tui.Features.Agents;
using Kautuka.Tui.Features.Auth;
using Kautuka.Tui.Features.Autopilot;
using Kautuka.Tui.Features.Chat;
using Kautuka.Tui.Features.Connectors;
using Kautuka.Tui.Features.Logs;
using Kautuka.Tui.Features.Memories;
using Kautuka.Tui.Features.Models;
using Kautuka.Tui.Features.Organization;
using Kautuka.Tui.Features.Providers;
using Kautuka.Tui.Features.Runtime;
using Kautuka.Tui.Features.Services;
using Kautuka.Tui.Features.Status;
using Kautuka.Tui.Features.Users;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Cli;

public sealed class TuiShell(
    WhoAmICommand whoAmICommand,
    StatusCommand statusCommand,
    ServicesCommand servicesCommand,
    LogsCommand logsCommand,
    RuntimeCommand runtimeCommand,
    AgentsCommand agentsCommand,
    AutopilotCommand autopilotCommand,
    AdvancedSettingsCommand advancedSettingsCommand,
    OrganizationCommand organizationCommand,
    ProvidersCommand providersCommand,
    ModelsCommand modelsCommand,
    ConnectorsCommand connectorsCommand,
    MemoriesCommand memoriesCommand,
    UsersCommand usersCommand)
{
    private readonly WhoAmICommand whoAmICommand = whoAmICommand;
    private readonly StatusCommand statusCommand = statusCommand;
    private readonly ServicesCommand servicesCommand = servicesCommand;
    private readonly LogsCommand logsCommand = logsCommand;
    private readonly RuntimeCommand runtimeCommand = runtimeCommand;
    private readonly AgentsCommand agentsCommand = agentsCommand;
    private readonly AutopilotCommand autopilotCommand = autopilotCommand;
    private readonly AdvancedSettingsCommand advancedSettingsCommand = advancedSettingsCommand;
    private readonly OrganizationCommand organizationCommand = organizationCommand;
    private readonly ProvidersCommand providersCommand = providersCommand;
    private readonly ModelsCommand modelsCommand = modelsCommand;
    private readonly ConnectorsCommand connectorsCommand = connectorsCommand;
    private readonly MemoriesCommand memoriesCommand = memoriesCommand;
    private readonly UsersCommand usersCommand = usersCommand;

    private AuthenticatedSession? authenticatedSession;
    private bool initialLoginPromptShown;
    private string? shellNotice;
    private string shellNoticeStyle = "grey";

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!initialLoginPromptShown)
            {
                initialLoginPromptShown = true;

                int promptExitCode = await ShowInitialLoginPromptAsync(cancellationToken).ConfigureAwait(false);
                if (promptExitCode >= 0)
                {
                    return promptExitCode;
                }
            }

            Result<IRenderable> dashboardResult = await statusCommand.BuildShellDashboardAsync(authenticatedSession?.Options, cancellationToken).ConfigureAwait(false);
            IRenderable dashboard = dashboardResult.IsSuccess && dashboardResult.Value is not null
                ? dashboardResult.Value
                : CreateMessagePanel("Status unavailable", dashboardResult.ErrorMessage ?? "Failed to inspect system state.", "red");

            List<ShellMenuOption> menuOptions =
            [
                authenticatedSession is null
                    ? new ShellMenuOption("Login", ShellAccessRequirement.Public)
                    : new ShellMenuOption("Logout", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Status", ShellAccessRequirement.Public),
                new ShellMenuOption("Logs", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Containers", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Package cache", ShellAccessRequirement.Admin),
                new ShellMenuOption("Who Am I", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Services", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Agents", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Chat", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Autopilot", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Memories", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Organization", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Advanced", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Providers", ShellAccessRequirement.Admin),
                new ShellMenuOption("Models", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Connectors", ShellAccessRequirement.Authenticated),
                new ShellMenuOption("Users", ShellAccessRequirement.Admin),
                new ShellMenuOption("Exit", ShellAccessRequirement.Public),
            ];

            ShellMenuOption selection = PromptShellMenu("Main Menu", menuOptions, cancellationToken, dashboard);

            switch (selection.Label)
            {
                case "Status":
                    break;
                case "Logs":
                    await RunLogsAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Containers":
                    await RunContainersAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Package cache":
                    await ShowPackageCacheScreenAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Who Am I":
                    await ShowCurrentUserScreenAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Services":
                    await ShowServicesScreenAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Agents":
                    await RunAgentsAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Chat":
                    await RunChatAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Autopilot":
                    await RunAutopilotAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Memories":
                    await RunMemoriesAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Organization":
                    await RunOrganizationAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Advanced":
                    await RunAdvancedAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Providers":
                    await RunProvidersAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Models":
                    await RunModelsAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Connectors":
                    await RunConnectorsAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Users":
                    await RunUsersAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Login":
                    await LoginAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Logout":
                    authenticatedSession = null;
                    ShowInfo("Cleared in-memory login session.");
                    break;
                case "Exit":
                    return 0;
            }
        }
    }

    private async Task<int> ShowInitialLoginPromptAsync(CancellationToken cancellationToken)
    {
        string selection = PromptShellChoice(
            "Welcome",
            ["Login", "Continue without login", "Exit"],
            cancellationToken,
            CreateWelcomePanel(),
            "Use arrow keys or number keys, then press Enter.");

        return selection switch
        {
            "Login" => await RunInitialLoginAsync(cancellationToken).ConfigureAwait(false),
            "Exit" => 0,
            _ => -1,
        };
    }

    private async Task<int> RunInitialLoginAsync(CancellationToken cancellationToken)
    {
        await LoginAsync(cancellationToken).ConfigureAwait(false);
        return -1;
    }

    private async Task RunLogsAsync(CancellationToken cancellationToken)
    {
        await ExecuteProtectedAsync(
            ShellAccessRequirement.Authenticated,
            (shell, _, token) =>
            {
                string service = shell.PromptShellTextInput("Logs", "Service key or unit name", token).Trim();
                int lineCount = shell.PromptShellIntInput("Logs", "Number of log lines", 80, token);

                return shell.logsCommand.ExecuteAsync([service, "--lines", lineCount.ToString()], token);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunContainersAsync(CancellationToken cancellationToken)
    {
        await ShowRuntimeScreenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunProvidersAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("Types", ShellAccessRequirement.Admin),
            new ShellMenuOption("Create", ShellAccessRequirement.Admin),
            new ShellMenuOption("Edit", ShellAccessRequirement.Admin),
            new ShellMenuOption("Delete", ShellAccessRequirement.Admin),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            IRenderable body = await BuildProvidersMenuBodyAsync(cancellationToken).ConfigureAwait(false);
            ShellMenuOption selection = PromptShellMenu("Providers", menuOptions, cancellationToken, body);

            switch (selection.Label)
            {
                case "Types":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.providersCommand.ExecuteAsync(session.Options, ["types"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Create":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.providersCommand.ExecuteAsync(session.Options, ["create"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Edit":
                    GatewayProviderListItem? editProvider = await SelectProviderAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (editProvider is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.providersCommand.ExecuteAsync(session.Options, ["edit", editProvider.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Delete":
                    GatewayProviderListItem? provider = await SelectProviderAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (provider is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.providersCommand.ExecuteAsync(session.Options, ["delete", provider.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunModelsAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("Create", ShellAccessRequirement.Admin),
            new ShellMenuOption("Edit", ShellAccessRequirement.Admin),
            new ShellMenuOption("Discover", ShellAccessRequirement.Admin),
            new ShellMenuOption("Probe", ShellAccessRequirement.Admin),
            new ShellMenuOption("Delete", ShellAccessRequirement.Admin),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            IRenderable body = await BuildModelsMenuBodyAsync(cancellationToken).ConfigureAwait(false);
            ShellMenuOption selection = PromptShellMenu("Models", menuOptions, cancellationToken, body);

            switch (selection.Label)
            {
                case "Create":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.modelsCommand.ExecuteAsync(session.Options, ["create"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Edit":
                    GatewayModelListItem? editModel = await SelectModelAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (editModel is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.modelsCommand.ExecuteAsync(session.Options, ["edit", editModel.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Discover":
                    GatewayProviderListItem? provider = await SelectProviderAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (provider is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.modelsCommand.ExecuteAsync(session.Options, ["discover", provider.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Probe":
                    GatewayModelListItem? probeModel = await SelectModelAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (probeModel is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.modelsCommand.ExecuteAsync(session.Options, ["probe", probeModel.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Delete":
                    GatewayModelListItem? model = await SelectModelAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (model is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.modelsCommand.ExecuteAsync(session.Options, ["delete", model.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunAgentsAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("List", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Show", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Create", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Edit", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Delete", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            ShellMenuOption selection = PromptShellMenu("Agents", menuOptions, cancellationToken);

            switch (selection.Label)
            {
                case "List":
                    await ShowAgentListScreenAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Show":
                    GatewayAgentListItem? showAgent = await SelectAgentAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (showAgent is null)
                    {
                        break;
                    }

                    PromptShellChoice(
                        $"Agent | {showAgent.Name}",
                        ["Back"],
                        cancellationToken,
                        AgentsCommand.BuildAgentDetailsRenderable(showAgent),
                        "Enter returns to agents menu");
                    break;
                case "Create":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Authenticated,
                        static async (shell, session, token) => await shell.agentsCommand.ExecuteAsync(session.Options, ["create"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Edit":
                    GatewayAgentListItem? editAgent = await SelectMutableAgentAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (editAgent is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Authenticated,
                        async (shell, session, token) => await shell.agentsCommand.ExecuteAsync(session.Options, ["edit", editAgent.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Delete":
                    GatewayAgentListItem? deleteAgent = await SelectMutableAgentAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (deleteAgent is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Authenticated,
                        async (shell, session, token) => await shell.agentsCommand.ExecuteAsync(session.Options, ["delete", deleteAgent.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunChatAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("Start New Session", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Resume Session", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("List Sessions", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("View Session", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            ShellMenuOption selection = PromptShellMenu("Chat", menuOptions, cancellationToken);

            switch (selection.Label)
            {
                case "Start New Session":
                    GatewayAgentListItem? startAgent = await SelectAgentAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (startAgent is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Authenticated,
                        async (_, session, token) => await ChatCommand.ExecuteAsync(session.Options, ["start", startAgent.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Resume Session":
                    GatewayAgentListItem? resumeAgent = await SelectAgentAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (resumeAgent is null)
                    {
                        break;
                    }

                    GatewaySession? resumeSession = await SelectAgentSessionAsync(resumeAgent.Id, ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (resumeSession is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Authenticated,
                        async (_, session, token) => await ChatCommand.ExecuteAsync(session.Options, ["resume", resumeAgent.Id, resumeSession.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "List Sessions":
                    GatewayAgentListItem? listAgent = await SelectAgentAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (listAgent is null)
                    {
                        break;
                    }

                    await ShowChatSessionsScreenAsync(listAgent, cancellationToken).ConfigureAwait(false);
                    break;
                case "View Session":
                    GatewayAgentListItem? viewAgent = await SelectAgentAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (viewAgent is null)
                    {
                        break;
                    }

                    GatewaySession? viewSession = await SelectAgentSessionAsync(viewAgent.Id, ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (viewSession is null)
                    {
                        break;
                    }

                    await ShowChatSessionScreenAsync(viewAgent, viewSession, cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunAutopilotAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("Schedules", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Runs", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Inbox", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            IRenderable body = await BuildAutopilotMenuBodyAsync(cancellationToken).ConfigureAwait(false);
            ShellMenuOption selection = PromptShellMenu("Autopilot", menuOptions, cancellationToken, body);

            switch (selection.Label)
            {
                case "Schedules":
                    await ShowAutopilotSchedulesScreenAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Runs":
                    await ShowAutopilotRunsScreenAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Inbox":
                    await ShowAutopilotInboxScreenAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunMemoriesAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("View Agent Memories", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            ShellMenuOption selection = PromptShellMenu(
                "Memories",
                menuOptions,
                cancellationToken,
                CreateMessagePanel("Memories", "Inspect durable facts and session history for a selected agent.", "grey"));

            switch (selection.Label)
            {
                case "View Agent Memories":
                    GatewayAgentListItem? agent = await SelectAgentAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (agent is null)
                    {
                        break;
                    }

                    await ShowAgentMemoriesScreenAsync(agent, cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunOrganizationAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("Show", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Edit", ShellAccessRequirement.Admin),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            ShellMenuOption selection = PromptShellMenu("Organization", menuOptions, cancellationToken);

            switch (selection.Label)
            {
                case "Show":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Authenticated,
                        static async (shell, session, token) => await shell.organizationCommand.ExecuteAsync(session.Options, ["show"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Edit":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.organizationCommand.ExecuteAsync(session.Options, ["edit"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunAdvancedAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("Show", ShellAccessRequirement.Admin),
            new ShellMenuOption("System Settings", ShellAccessRequirement.Admin),
            new ShellMenuOption("Provider Path Policy", ShellAccessRequirement.Admin),
            new ShellMenuOption("Memory", ShellAccessRequirement.Admin),
            new ShellMenuOption("Persona Definitions", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            ShellMenuOption selection = PromptShellMenu("Advanced", menuOptions, cancellationToken);

            switch (selection.Label)
            {
                case "Show":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.advancedSettingsCommand.ExecuteAsync(session.Options, ["show"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "System Settings":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.advancedSettingsCommand.ExecuteAsync(session.Options, ["edit-system"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Provider Path Policy":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.advancedSettingsCommand.ExecuteAsync(session.Options, ["edit-provider-path"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Memory":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.advancedSettingsCommand.ExecuteAsync(session.Options, ["edit-memory"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Persona Definitions":
                    await RunPersonaDefinitionsAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunPersonaDefinitionsAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("Show", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Edit", ShellAccessRequirement.Admin),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            ShellMenuOption selection = PromptShellMenu("Persona Definitions", menuOptions, cancellationToken);

            switch (selection.Label)
            {
                case "Show":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Authenticated,
                        static async (shell, session, token) => await shell.advancedSettingsCommand.ExecuteAsync(session.Options, ["show-persona"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Edit":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.advancedSettingsCommand.ExecuteAsync(session.Options, ["edit-persona"], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunConnectorsAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("Show", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Delete", ShellAccessRequirement.Authenticated),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            IRenderable body = await BuildConnectorsMenuBodyAsync(cancellationToken).ConfigureAwait(false);
            ShellMenuOption selection = PromptShellMenu("Connectors", menuOptions, cancellationToken, body);

            switch (selection.Label)
            {
                case "Show":
                    GatewayConnectorListItem? showConnector = await SelectConnectorAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (showConnector is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Authenticated,
                        async (shell, session, token) => await shell.connectorsCommand.ExecuteAsync(session.Options, ["show", showConnector.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Delete":
                    GatewayConnectorListItem? deleteConnector = await SelectManageableConnectorAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
                    if (deleteConnector is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Authenticated,
                        async (shell, session, token) => await shell.connectorsCommand.ExecuteAsync(session.Options, ["delete", deleteConnector.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunUsersAsync(CancellationToken cancellationToken)
    {
        List<ShellMenuOption> menuOptions =
        [
            new ShellMenuOption("List", ShellAccessRequirement.Admin),
            new ShellMenuOption("Invite", ShellAccessRequirement.Admin),
            new ShellMenuOption("Activate", ShellAccessRequirement.Admin),
            new ShellMenuOption("Deactivate", ShellAccessRequirement.Admin),
            new ShellMenuOption("Set Role", ShellAccessRequirement.Admin),
            new ShellMenuOption("Set Import Permissions", ShellAccessRequirement.Admin),
            new ShellMenuOption("Back", ShellAccessRequirement.Public),
        ];

        while (true)
        {
            ShellMenuOption selection = PromptShellMenu("Users", menuOptions, cancellationToken);

            switch (selection.Label)
            {
                case "List":
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        static async (shell, session, token) => await shell.usersCommand.ExecuteAsync(session.Options, [], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Invite":
                    await RunInviteUserAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "Activate":
                    GatewayUserListItem? activateUser = await SelectUserAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (activateUser is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.usersCommand.ExecuteAsync(session.Options, ["activate", activateUser.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Deactivate":
                    GatewayUserListItem? deactivateUser = await SelectUserAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (deactivateUser is null)
                    {
                        break;
                    }

                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.usersCommand.ExecuteAsync(session.Options, ["deactivate", deactivateUser.Id], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Set Role":
                    GatewayUserListItem? roleUser = await SelectUserAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (roleUser is null)
                    {
                        break;
                    }

                    string role = PromptShellChoice("Role", ["Admin", "Member"], cancellationToken);
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.usersCommand.ExecuteAsync(session.Options, ["set-role", roleUser.Id, role], token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Set Import Permissions":
                    GatewayUserListItem? permissionUser = await SelectUserAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
                    if (permissionUser is null)
                    {
                        break;
                    }

                    bool canImportConnectors = PromptShellConfirm("Import Permissions", "Allow connector imports?", cancellationToken, permissionUser.CanImportConnectors);
                    bool canImportSkills = PromptShellConfirm("Import Permissions", "Allow skill imports?", cancellationToken, permissionUser.CanImportSkills);
                    await ExecuteProtectedAsync(
                        ShellAccessRequirement.Admin,
                        async (shell, session, token) => await shell.usersCommand.ExecuteAsync(
                            session.Options,
                            ["set-imports", permissionUser.Id, canImportConnectors ? "true" : "false", canImportSkills ? "true" : "false"],
                            token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "Back":
                    return;
            }
        }
    }

    private async Task RunInviteUserAsync(CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ShowError(authResult.ErrorMessage ?? "Administrator access is required.");
            return;
        }

        string username = PromptShellTextInput("Invite User", "Username", cancellationToken).Trim();
        string password = PromptShellTextInput("Invite User", "Password", cancellationToken, secret: true);
        string email = PromptShellTextInput("Invite User", "Email (optional)", cancellationToken).Trim();
        string role = PromptShellChoice("Role", ["Admin", "Member"], cancellationToken);

        await ExecuteProtectedAsync(
            ShellAccessRequirement.Admin,
            async (shell, session, token) => await shell.usersCommand.ExecuteAsync(
                session.Options,
                string.IsNullOrWhiteSpace(email)
                    ? ["invite", username, password, role]
                    : ["invite", username, password, role, email],
                token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> loginResult = await PromptForLoginAsync(cancellationToken).ConfigureAwait(false);
        if (!loginResult.IsSuccess || loginResult.Value is null)
        {
            ShowError(loginResult.ErrorMessage ?? "Login failed.");
            return;
        }

        authenticatedSession = loginResult.Value;
        ShowInfo($"Login succeeded for {authenticatedSession.User.Username} ({authenticatedSession.User.Role}).");
    }

    private async Task ShowChatSessionsScreenAsync(GatewayAgentListItem agent, CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ShowError(authResult.ErrorMessage ?? "Access denied.");
            return;
        }

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            ShowError(clientResult.ErrorMessage ?? "Authentication failed.");
            return;
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewaySessionsResponse> sessionsResult = await client.GetSessionsForAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        if (!sessionsResult.IsSuccess || sessionsResult.Value is null)
        {
            ShowError(sessionsResult.ErrorMessage ?? "Failed to load sessions.");
            return;
        }

        PromptShellChoice(
            $"Chat Sessions | {agent.Name}",
            ["Back"],
            cancellationToken,
            ChatCommand.BuildSessionsRenderable(agent.Id, sessionsResult.Value.Sessions),
            "Enter returns to chat menu");
    }

    private async Task ShowCurrentUserScreenAsync(CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ShowError(authResult.ErrorMessage ?? "Access denied.");
            return;
        }

        Result<GatewayUserPayload> userResult = await whoAmICommand.GetCurrentUserAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        if (!userResult.IsSuccess || userResult.Value is null)
        {
            ShowError(userResult.ErrorMessage ?? "Failed to load current user.");
            return;
        }

        PromptShellChoice(
            "Who Am I",
            ["Back"],
            cancellationToken,
            WhoAmICommand.BuildUserRenderable(userResult.Value),
            "Enter returns to main menu");
    }

    private async Task ShowServicesScreenAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ServiceUnitStatus>> servicesResult = await servicesCommand.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (!servicesResult.IsSuccess || servicesResult.Value is null)
        {
            ShowError(servicesResult.ErrorMessage ?? "Failed to inspect services.");
            return;
        }

        PromptShellChoice(
            "Services",
            ["Back"],
            cancellationToken,
            ServicesCommand.BuildServicesRenderable(servicesResult.Value),
            "Enter returns to main menu");
    }

    private async Task ShowRuntimeScreenAsync(CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ShowError(authResult.ErrorMessage ?? "Access denied.");
            return;
        }

        Result<RuntimeSummary> runtimeResult = await runtimeCommand.GetSummaryAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        if (!runtimeResult.IsSuccess || runtimeResult.Value is null)
        {
            ShowError(runtimeResult.ErrorMessage ?? "Failed to inspect runtime state.");
            return;
        }

        PromptShellChoice(
            "Containers",
            ["Back"],
            cancellationToken,
            RuntimeCommand.BuildRuntimeRenderable(runtimeResult.Value),
            "Enter returns to main menu");
    }

    private async Task ShowPackageCacheScreenAsync(CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ShowError(authResult.ErrorMessage ?? "Administrator access is required.");
            return;
        }

        Result<PackageCacheSummary> cacheResult = await runtimeCommand.GetPackageCacheAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        if (!cacheResult.IsSuccess || cacheResult.Value is null)
        {
            ShowError(cacheResult.ErrorMessage ?? "Failed to read package proxy cache stats.");
            return;
        }

        PromptShellChoice(
            "Package cache",
            ["Back"],
            cancellationToken,
            RuntimeCommand.BuildPackageCacheRenderable(cacheResult.Value),
            "Enter returns to main menu");
    }

    private async Task ShowAgentListScreenAsync(CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ShowError(authResult.ErrorMessage ?? "Access denied.");
            return;
        }

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            ShowError(clientResult.ErrorMessage ?? "Authentication failed.");
            return;
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayAgentsResponse> agentsResult = await client.GetAgentsAsync(cancellationToken).ConfigureAwait(false);
        if (!agentsResult.IsSuccess || agentsResult.Value is null)
        {
            ShowError(agentsResult.ErrorMessage ?? "Failed to load agents.");
            return;
        }

        PromptShellChoice(
            "Agents",
            ["Back"],
            cancellationToken,
            AgentsCommand.BuildAgentListRenderable(agentsResult.Value.Agents),
            "Enter returns to agents menu");
    }

    private async Task ShowAutopilotSchedulesScreenAsync(CancellationToken cancellationToken)
    {
        Result<(IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)> result = await ExecuteProtectedAsync(
            ShellAccessRequirement.Authenticated,
            async (shell, session, token) => await shell.autopilotCommand.LoadSchedulesWithAgentNamesAsync(session.Options, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            ShowError(result.ErrorMessage ?? "Failed to load autopilot schedules.");
            return;
        }

        PromptShellChoice(
            "Autopilot | Schedules",
            ["Back"],
            cancellationToken,
            autopilotCommand.BuildSchedulesRenderable(result.Value.Schedules, result.Value.AgentNames),
            "Enter returns to autopilot menu");
    }

    private async Task ShowAutopilotRunsScreenAsync(CancellationToken cancellationToken)
    {
        Result<(IReadOnlyList<GatewayScheduledRun> Runs, IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)> result = await ExecuteProtectedAsync(
            ShellAccessRequirement.Authenticated,
            async (shell, session, token) => await shell.autopilotCommand.LoadRunsWithSchedulesAsync(session.Options, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            ShowError(result.ErrorMessage ?? "Failed to load autopilot runs.");
            return;
        }

        PromptShellChoice(
            "Autopilot | Runs",
            ["Back"],
            cancellationToken,
            autopilotCommand.BuildRunsRenderable(result.Value.Runs, result.Value.Schedules, result.Value.AgentNames),
            "Enter returns to autopilot menu");
    }

    private async Task ShowAutopilotInboxScreenAsync(CancellationToken cancellationToken)
    {
        Result<(IReadOnlyList<GatewayAutopilotNotification> Notifications, IReadOnlyList<GatewayScheduledRun> Runs, IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)> result = await ExecuteProtectedAsync(
            ShellAccessRequirement.Authenticated,
            async (shell, session, token) => await shell.autopilotCommand.LoadInboxAsync(session.Options, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            ShowError(result.ErrorMessage ?? "Failed to load autopilot inbox.");
            return;
        }

        PromptShellChoice(
            "Autopilot | Inbox",
            ["Back"],
            cancellationToken,
            autopilotCommand.BuildInboxRenderable(result.Value.Notifications, result.Value.Runs, result.Value.Schedules, result.Value.AgentNames),
            "Enter returns to autopilot menu");
    }

    private async Task ShowAgentMemoriesScreenAsync(GatewayAgentListItem agent, CancellationToken cancellationToken)
    {
        Result<(GatewayMemoriesResponse Memories, string AgentName)> result = await ExecuteProtectedAsync(
            ShellAccessRequirement.Authenticated,
            async (shell, session, token) => await shell.memoriesCommand.LoadAgentMemoriesAsync(session.Options, agent.Id, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            ShowError(result.ErrorMessage ?? "Failed to load memories.");
            return;
        }

        PromptShellChoice(
            $"Memories | {agent.Name}",
            ["Back"],
            cancellationToken,
            memoriesCommand.BuildMemoriesRenderable(result.Value.AgentName, result.Value.Memories),
            "Enter returns to memories menu");
    }

    private async Task<IRenderable> BuildProvidersMenuBodyAsync(CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Admin, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            return CreateMessagePanel("Providers", authResult.ErrorMessage ?? "Administrator access is required.", "red");
        }

        Result<IReadOnlyList<GatewayProviderListItem>> providersResult = await providersCommand.GetProvidersAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        return !providersResult.IsSuccess || providersResult.Value is null
            ? CreateMessagePanel("Providers", providersResult.ErrorMessage ?? "Failed to load providers.", "red")
            : ProvidersCommand.BuildProvidersRenderable(providersResult.Value);
    }

    private async Task<IRenderable> BuildModelsMenuBodyAsync(CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            return CreateMessagePanel("Models", authResult.ErrorMessage ?? "Access denied.", "red");
        }

        Result<IReadOnlyList<GatewayModelListItem>> modelsResult = await modelsCommand.GetModelsAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        return !modelsResult.IsSuccess || modelsResult.Value is null
            ? CreateMessagePanel("Models", modelsResult.ErrorMessage ?? "Failed to load models.", "red")
            : ModelsCommand.BuildModelsRenderable(modelsResult.Value);
    }

    private async Task<IRenderable> BuildConnectorsMenuBodyAsync(CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            return CreateMessagePanel("Connectors", authResult.ErrorMessage ?? "Access denied.", "red");
        }

        Result<IReadOnlyList<GatewayConnectorListItem>> connectorsResult = await connectorsCommand.GetConnectorsAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        return !connectorsResult.IsSuccess || connectorsResult.Value is null
            ? CreateMessagePanel("Connectors", connectorsResult.ErrorMessage ?? "Failed to load connectors.", "red")
            : ConnectorsCommand.BuildConnectorsRenderable(connectorsResult.Value);
    }

    private async Task<IRenderable> BuildAutopilotMenuBodyAsync(CancellationToken cancellationToken)
    {
        Result<(IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)> result = await ExecuteProtectedAsync(
            ShellAccessRequirement.Authenticated,
            async (shell, session, token) => await shell.autopilotCommand.LoadSchedulesWithAgentNamesAsync(session.Options, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? autopilotCommand.BuildSchedulesRenderable(result.Value.Schedules, result.Value.AgentNames)
            : CreateMessagePanel("Autopilot", result.ErrorMessage ?? "Failed to load autopilot schedules.", "red");
    }

    private async Task ShowChatSessionScreenAsync(GatewayAgentListItem agent, GatewaySession session, CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(ShellAccessRequirement.Authenticated, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ShowError(authResult.ErrorMessage ?? "Access denied.");
            return;
        }

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            ShowError(clientResult.ErrorMessage ?? "Authentication failed.");
            return;
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewaySession> sessionResult = await client.GetSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
        if (!sessionResult.IsSuccess || sessionResult.Value is null)
        {
            ShowError(sessionResult.ErrorMessage ?? "Failed to load session.");
            return;
        }

        PromptShellChoice(
            $"Chat Session | {agent.Name}",
            ["Back"],
            cancellationToken,
            ChatCommand.BuildSessionRenderable(sessionResult.Value),
            "Enter returns to chat menu");
    }

    private async Task ExecuteProtectedAsync(
        ShellAccessRequirement requiredAccess,
        Func<TuiShell, AuthenticatedSession, CancellationToken, Task<Result>> action,
        CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(requiredAccess, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ShowError(authResult.ErrorMessage ?? "Access denied.");
            return;
        }

        Result result = await action(this, authResult.Value, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return;
        }

        ShowError(result.ErrorMessage ?? "Command failed.");
    }

    private async Task<Result<T>> ExecuteProtectedAsync<T>(
        ShellAccessRequirement requiredAccess,
        Func<TuiShell, AuthenticatedSession, CancellationToken, Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(requiredAccess, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            return Results.Failure<T>(authResult.ErrorMessage ?? "Access denied.");
        }

        return await action(this, authResult.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<AuthenticatedSession>> EnsureAuthorizedSessionAsync(ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        if (requiredAccess is ShellAccessRequirement.Public)
        {
            return Results.Failure<AuthenticatedSession>("Public actions do not require authenticated session handling.");
        }

        Result<AuthenticatedSession> sessionResult = await EnsureAuthenticatedSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!sessionResult.IsSuccess || sessionResult.Value is null)
        {
            return sessionResult;
        }

        if (!ShellAccessEvaluator.HasAccess(requiredAccess, sessionResult.Value.User))
        {
            return Results.Failure<AuthenticatedSession>(ShellAccessEvaluator.GetAccessDeniedMessage(requiredAccess));
        }

        return sessionResult;
    }

    private async Task<Result<AuthenticatedSession>> EnsureAuthenticatedSessionAsync(CancellationToken cancellationToken)
    {
        if (authenticatedSession is not null)
        {
            Result<GatewayUserPayload> validationResult = await ValidateAuthenticationAsync(authenticatedSession.Options, cancellationToken).ConfigureAwait(false);
            if (validationResult.IsSuccess && validationResult.Value is not null)
            {
                authenticatedSession = authenticatedSession with { User = validationResult.Value };
                return Results.Success(authenticatedSession);
            }

            authenticatedSession = null;
        }

        Result<AuthenticatedSession> loginResult = await PromptForLoginAsync(cancellationToken).ConfigureAwait(false);
        if (!loginResult.IsSuccess || loginResult.Value is null)
        {
            return loginResult;
        }

        authenticatedSession = loginResult.Value;
        return Results.Success(authenticatedSession);
    }

    private async Task<GatewayProviderListItem?> SelectProviderAsync(ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        return await SelectProtectedItemAsync(
            requiredAccess,
            "Select provider",
            async (client, token) =>
            {
                Result<GatewayProvidersResponse> result = await client.GetProvidersAsync(token).ConfigureAwait(false);
                return !result.IsSuccess || result.Value is null
                    ? Results.Failure<IReadOnlyList<GatewayProviderListItem>>(result.ErrorMessage ?? "Failed to load providers.")
                    : Results.Success(result.Value.Providers);
            },
            provider => $"{provider.Type} | {provider.Id} | API key {(provider.HasApiKey ? "set" : "missing")}",
            "No providers found.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GatewayModelListItem?> SelectModelAsync(ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        return await SelectProtectedItemAsync(
            requiredAccess,
            "Select model",
            async (client, token) =>
            {
                Result<GatewayModelsResponse> result = await client.GetModelsAsync(token).ConfigureAwait(false);
                return !result.IsSuccess || result.Value is null
                    ? Results.Failure<IReadOnlyList<GatewayModelListItem>>(result.ErrorMessage ?? "Failed to load models.")
                    : Results.Success(result.Value.Models);
            },
            model => $"{model.ModelName} | {model.ProviderId} | {model.Id}",
            "No models found.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GatewayConnectorListItem?> SelectConnectorAsync(ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        return await SelectProtectedItemAsync(
            requiredAccess,
            "Select connector",
            async (client, token) =>
            {
                Result<GatewayConnectorsResponse> result = await client.GetConnectorsAsync(token).ConfigureAwait(false);
                return !result.IsSuccess || result.Value is null
                    ? Results.Failure<IReadOnlyList<GatewayConnectorListItem>>(result.ErrorMessage ?? "Failed to load connectors.")
                    : Results.Success(result.Value.Connectors);
            },
            connector => $"{connector.Name} | {connector.Id} | {(connector.Enabled ? "enabled" : "disabled")}",
            "No connectors found.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GatewayAgentListItem?> SelectAgentAsync(ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        return await SelectProtectedItemAsync(
            requiredAccess,
            "Select agent",
            async (client, token) =>
            {
                Result<GatewayAgentsResponse> result = await client.GetAgentsAsync(token).ConfigureAwait(false);
                return !result.IsSuccess || result.Value is null
                    ? Results.Failure<IReadOnlyList<GatewayAgentListItem>>(result.ErrorMessage ?? "Failed to load agents.")
                    : Results.Success(result.Value.Agents);
            },
            agent => $"{agent.Name} | {agent.Id} | {agent.ModelId}",
            "No agents found.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GatewayAgentListItem?> SelectMutableAgentAsync(ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        return await SelectProtectedItemAsync(
            requiredAccess,
            "Select agent",
            async (client, token) =>
            {
                Result<GatewayAgentsResponse> result = await client.GetAgentsAsync(token).ConfigureAwait(false);
                return !result.IsSuccess || result.Value is null
                    ? Results.Failure<IReadOnlyList<GatewayAgentListItem>>(result.ErrorMessage ?? "Failed to load agents.")
                    : Results.Success(result.Value.Agents);
            },
            agent => $"{agent.Name} | {agent.Id} | {agent.ModelId}",
            "No editable agents found.",
            cancellationToken,
            static (user, agent) => ShellAccessEvaluator.CanMutateAgent(user, agent)).ConfigureAwait(false);
    }

    private async Task<GatewaySession?> SelectAgentSessionAsync(string agentId, ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        return await SelectProtectedItemAsync(
            requiredAccess,
            "Select session",
            async (client, token) =>
            {
                Result<GatewaySessionsResponse> result = await client.GetSessionsForAgentAsync(agentId, token).ConfigureAwait(false);
                return !result.IsSuccess || result.Value is null
                    ? Results.Failure<IReadOnlyList<GatewaySession>>(result.ErrorMessage ?? "Failed to load sessions.")
                    : Results.Success(result.Value.Sessions);
            },
            session => $"{session.UpdatedAt} | {session.Messages.Count} messages | {session.Id}",
            "No sessions found for that agent.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GatewayConnectorListItem?> SelectManageableConnectorAsync(ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        return await SelectProtectedItemAsync(
            requiredAccess,
            "Select connector to delete",
            async (client, token) =>
            {
                Result<GatewayConnectorsResponse> result = await client.GetConnectorsAsync(token).ConfigureAwait(false);
                return !result.IsSuccess || result.Value is null
                    ? Results.Failure<IReadOnlyList<GatewayConnectorListItem>>(result.ErrorMessage ?? "Failed to load connectors.")
                    : Results.Success(result.Value.Connectors);
            },
            connector => $"{connector.Name} | {connector.Id} | {(connector.Enabled ? "enabled" : "disabled")}",
            "No connectors found that you can delete.",
            cancellationToken,
            static (user, connector) => ShellAccessEvaluator.CanManageConnector(user, connector)).ConfigureAwait(false);
    }

    private async Task<GatewayUserListItem?> SelectUserAsync(ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        return await SelectProtectedItemAsync(
            requiredAccess,
            "Select user",
            async (client, token) =>
            {
                Result<GatewayUsersResponse> result = await client.GetUsersAsync(token).ConfigureAwait(false);
                return !result.IsSuccess || result.Value is null
                    ? Results.Failure<IReadOnlyList<GatewayUserListItem>>(result.ErrorMessage ?? "Failed to load users.")
                    : Results.Success(result.Value.Users);
            },
            user => $"{user.Username} | {user.Role} | {(user.IsActive ? "active" : "inactive")} | {user.Id}",
            "No users found.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> SelectProtectedItemAsync<T>(
        ShellAccessRequirement requiredAccess,
        string title,
        Func<GatewayApiClient, CancellationToken, Task<Result<IReadOnlyList<T>>>> loader,
        Func<T, string> labelFactory,
        string emptyMessage,
        CancellationToken cancellationToken,
        Func<GatewayUserPayload, T, bool>? itemFilter = null)
        where T : class
    {
        Result<AuthenticatedSession> authResult = await EnsureAuthorizedSessionAsync(requiredAccess, cancellationToken).ConfigureAwait(false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ShowError(authResult.ErrorMessage ?? "Access denied.");
            return null;
        }

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(authResult.Value.Options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            ShowError(clientResult.ErrorMessage ?? "Authentication failed.");
            return null;
        }

        using GatewayApiClient client = clientResult.Value;
        Result<IReadOnlyList<T>> itemsResult = await loader(client, cancellationToken).ConfigureAwait(false);
        if (!itemsResult.IsSuccess || itemsResult.Value is null)
        {
            ShowError(itemsResult.ErrorMessage ?? "Failed to load choices.");
            return null;
        }

        IReadOnlyList<T> visibleItems = itemFilter is null
            ? itemsResult.Value
            : [.. itemsResult.Value.Where(item => itemFilter(authResult.Value.User, item))];

        if (visibleItems.Count == 0)
        {
            ShowInfo(emptyMessage);
            return null;
        }

        List<SelectionItem<T>> choices = [.. visibleItems.Select(item => new SelectionItem<T>(labelFactory(item), item))];
        choices.Add(new SelectionItem<T>("Back", null));

        SelectionItem<T> selected = PromptShellChoice(title, choices, cancellationToken, CreateSelectionPanel(title, visibleItems.Count));

        return selected.Value;
    }

    private async Task<Result<AuthenticatedSession>> PromptForLoginAsync(CancellationToken cancellationToken)
    {
        string authMode = PromptShellChoice(
            "Authentication Method",
            ["Username and password", "Access token", "Cancel"],
            cancellationToken,
            CreateWelcomePanel());

        if (authMode == "Cancel")
        {
            return Results.Failure<AuthenticatedSession>("Authentication was cancelled.");
        }

        TuiCommandOptions options = authMode switch
        {
            "Access token" => new TuiCommandOptions(
                TuiCommandOptions.ResolveGatewayUrl(),
                PromptShellTextInput("Authentication", "Access token", cancellationToken, secret: true),
                null,
                null,
                []),
            _ => new TuiCommandOptions(
                TuiCommandOptions.ResolveGatewayUrl(),
                null,
                PromptShellTextInput("Authentication", "Username", cancellationToken).Trim(),
                PromptShellTextInput("Authentication", "Password", cancellationToken, secret: true),
                []),
        };

        Result<GatewayUserPayload> validationResult = await ValidateAuthenticationAsync(options, cancellationToken).ConfigureAwait(false);
        return validationResult.IsSuccess && validationResult.Value is not null
            ? Results.Success(new AuthenticatedSession(options, validationResult.Value))
            : Results.Failure<AuthenticatedSession>(validationResult.ErrorMessage ?? "Authentication failed.");
    }

    private static async Task<Result<GatewayUserPayload>> ValidateAuthenticationAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<GatewayUserPayload>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayUserPayload> meResult = await client.GetMeAsync(cancellationToken).ConfigureAwait(false);
        return meResult.IsSuccess && meResult.Value is not null
            ? meResult
            : Results.Failure<GatewayUserPayload>(meResult.ErrorMessage ?? "Authentication failed.");
    }

    private IEnumerable<ShellMenuOption> GetVisibleMenuOptions(IEnumerable<ShellMenuOption> options)
        => options.Where(option => CanAccess(option.AccessRequirement));

    private ShellMenuOption PromptShellMenu(string title, List<ShellMenuOption> options, CancellationToken cancellationToken, IRenderable? body = null)
        => PromptShellChoice(title, [.. GetVisibleMenuOptions(options)], cancellationToken, body ?? CreateSectionPanel(title));

    private T PromptShellChoice<T>(string title, List<T> choices, CancellationToken cancellationToken, IRenderable? body = null, string? footer = null)
        where T : class
        => RunScreen(new ShellChoiceScreen<T>(
            title,
            body ?? CreateSectionPanel(title),
            choices,
            footer ?? "↑/↓ move  •  Enter select  •  Number keys jump"), cancellationToken);

    private T RunScreen<T>(IShellScreen<T> screen, CancellationToken cancellationToken)
        where T : class
    {
        T? selection = default;

        AnsiConsole.Clear();

        AnsiConsole.Live(BuildShellScreen(
                screen.Title,
                screen.Body,
                screen.Navigation,
                screen.Footer))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Crop)
            .Cropping(VerticalOverflowCropping.Top)
            .Start(context =>
            {
                context.Refresh();

                while (selection is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                    if (screen.TryHandleKey(key, out T? screenSelection))
                    {
                        selection = screenSelection;
                        continue;
                    }

                    context.UpdateTarget(BuildShellScreen(
                        screen.Title,
                        screen.Body,
                        screen.Navigation,
                        screen.Footer));
                    context.Refresh();
                }
            });

        if (selection is not null)
        {
            return selection;
        }

        throw new InvalidOperationException($"No selection produced for '{screen.Title}'.");
    }

    private Panel BuildShellScreen(string title, IRenderable body, IRenderable menuPanel, string footer)
    {
        Grid headerGrid = new();
        headerGrid.AddColumn();
        headerGrid.AddColumn();
        headerGrid.AddRow(CreateAsciiBanner(), CreateShellMetaPanel(title));

        Grid contentGrid = new();
        contentGrid.AddColumn();
        contentGrid.AddColumn();
        contentGrid.AddRow(
            new Panel(menuPanel)
            {
                Header = new PanelHeader("Navigation", Justify.Left),
                Border = BoxBorder.Rounded,
                Expand = true,
            },
            new Panel(body)
            {
                Header = new PanelHeader("Workspace", Justify.Left),
                Border = BoxBorder.Rounded,
                Expand = true,
            });

        Panel shellPanel = new(new Rows(
            headerGrid,
            new Rule().RuleStyle("grey"),
            contentGrid,
            new Markup(BuildFooterMarkup(footer))))
        {
            Border = BoxBorder.Double,
            Header = new PanelHeader("[bold deepskyblue1]Kautuka Shell[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true,
        };

        return shellPanel;
    }

    private static FigletText CreateAsciiBanner()
        => new FigletText("Kautuka")
        {
            Color = Color.DeepSkyBlue1,
            Justification = Justify.Left,
            Pad = false,
        };

    private Panel CreateShellMetaPanel(string title)
    {
        string identity = authenticatedSession is null
            ? "guest"
            : $"{authenticatedSession.User.Username} ({authenticatedSession.User.Role})";

        string body = $"[grey]Section:[/] {Markup.Escape(title)}\n[grey]Identity:[/] {Markup.Escape(identity)}\n[grey]Gateway:[/] {Markup.Escape(TuiCommandOptions.ResolveGatewayUrl().ToString())}";
        return new Panel(body)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("Session", Justify.Left),
            Expand = true,
        };
    }

    private static Panel CreateWelcomePanel()
        => CreateMessagePanel("Welcome", "Sign in to unlock Gateway-backed administration, or continue as a guest for local appliance actions.", "deepskyblue1");

    private Panel CreateSectionPanel(string title)
    {
        string identity = authenticatedSession is null ? "Guest session" : $"Signed in as {authenticatedSession.User.Username}";
        return CreateMessagePanel(title, $"{identity}. Choose an action from the navigation panel to continue.", "grey");
    }

    private static Panel CreateSelectionPanel(string title, int availableCount)
        => CreateMessagePanel(title, $"{availableCount} selectable item(s) available. Choose one to continue.", "grey");

    private static Panel CreateMessagePanel(string title, string message, string borderColor)
        => new(message)
        {
            Header = new PanelHeader(Markup.Escape(title), Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse(borderColor),
            Expand = true,
        };

    private string PromptShellTextInput(string title, string prompt, CancellationToken cancellationToken, bool secret = false, string? initialValue = null)
        => RunScreen(new ShellTextInputScreen(
            title,
            CreateSectionPanel(title),
            prompt,
            secret,
            "Type to edit  •  Enter submits",
            initialValue), cancellationToken);

    private int PromptShellIntInput(string title, string prompt, int defaultValue, CancellationToken cancellationToken)
    {
        string currentValue = defaultValue.ToString();

        while (true)
        {
            string rawValue = PromptShellTextInput(title, prompt, cancellationToken, initialValue: currentValue).Trim();
            if (int.TryParse(rawValue, out int parsedValue) && parsedValue > 0)
            {
                return parsedValue;
            }

            currentValue = rawValue;
            ShowError("Enter a positive integer value.");
        }
    }

    private bool PromptShellConfirm(string title, string message, CancellationToken cancellationToken, bool defaultYes = false)
    {
        List<string> choices = defaultYes ? ["Yes", "No"] : ["No", "Yes"];
        string selection = PromptShellChoice(title, choices, cancellationToken, CreateMessagePanel(title, message, "yellow"), "Enter selects");
        return string.Equals(selection, "Yes", StringComparison.Ordinal);
    }

    private string BuildFooterMarkup(string footer)
    {
        if (string.IsNullOrWhiteSpace(shellNotice))
        {
            return $"[grey]{Markup.Escape(footer)}[/]";
        }

        return $"[grey]{Markup.Escape(footer)}[/]  [dim]•[/]  [{shellNoticeStyle}]{Markup.Escape(shellNotice)}[/]";
    }

    private void SetShellNotice(string message, string style)
    {
        shellNotice = message;
        shellNoticeStyle = style;
    }

    private bool CanAccess(ShellAccessRequirement requiredAccess)
        => ShellAccessEvaluator.HasAccess(requiredAccess, authenticatedSession?.User);

    private sealed record AuthenticatedSession(TuiCommandOptions Options, GatewayUserPayload User);

    private sealed record ShellMenuOption(string Label, ShellAccessRequirement AccessRequirement)
    {
        public override string ToString() => Label;
    }

    private void ShowError(string message)
    {
        SetShellNotice(message, "red");
    }

    private void ShowInfo(string message)
    {
        SetShellNotice(message, "green");
    }

    private void ShowResult(Result result)
    {
        if (result.IsSuccess)
        {
            return;
        }

        ShowError(result.ErrorMessage ?? "Command failed.");
    }

}


internal sealed record SelectionItem<T>(string Label, T? Value)
    where T : class
{
    public override string ToString() => Label;
}

internal enum ShellAccessRequirement
{
    Public,
    Authenticated,
    Admin,
}

internal static class ShellAccessEvaluator
{
    public static bool HasAccess(ShellAccessRequirement requiredAccess, GatewayUserPayload? user)
        => requiredAccess switch
        {
            ShellAccessRequirement.Public => true,
            ShellAccessRequirement.Authenticated => user is not null,
            ShellAccessRequirement.Admin => user is not null && string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    public static string GetAccessDeniedMessage(ShellAccessRequirement requiredAccess)
        => requiredAccess switch
        {
            ShellAccessRequirement.Admin => "Administrator access is required for this action.",
            ShellAccessRequirement.Authenticated => "Authentication is required for this action.",
            _ => "Access denied.",
        };

    public static bool CanManageConnector(GatewayUserPayload user, GatewayConnectorListItem connector)
        => string.Equals(connector.Scope, "system", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
            : string.Equals(user.Id, connector.OwnerUserId, StringComparison.Ordinal);

    public static bool CanManageAgent(GatewayUserPayload user, GatewayAgentListItem agent)
        => string.Equals(agent.Scope, "system", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
            : string.Equals(user.Id, agent.OwnerUserId, StringComparison.Ordinal);

    public static bool CanMutateAgent(GatewayUserPayload user, GatewayAgentListItem agent)
        => CanManageAgent(user, agent);
}
