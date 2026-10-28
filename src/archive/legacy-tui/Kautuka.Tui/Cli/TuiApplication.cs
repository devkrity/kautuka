// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Cli/TuiApplication.cs | Namespace: Kautuka.Tui.Cli
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
using Kautuka.Tui.Infrastructure.Processes;

using Spectre.Console;

namespace Kautuka.Tui.Cli;

public sealed class TuiApplication(
    TuiShell shell,
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
    private readonly TuiShell shell = shell;
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

    private static readonly IReadOnlyList<CliCommandDefinition> commandDefinitions =
    [
        CliCommandDefinition.Public(
            "shell",
            "shell",
            "Launch the interactive TUI shell."),
        CliCommandDefinition.Public(
            "status",
            "status",
            "Show a compact appliance overview."),
        CliCommandDefinition.Public(
            "help",
            "help",
            "Show available commands and access requirements.",
            ["--help", "-h"]),
        CliCommandDefinition.Gateway(
            "whoami",
            "whoami",
            "Show the current authenticated Gateway identity and role.",
            static _ => ShellAccessRequirement.Authenticated),
        CliCommandDefinition.Gateway(
            "services",
            "services",
            "List Kautuka systemd services and their current state.",
            static _ => ShellAccessRequirement.Authenticated),
        CliCommandDefinition.Gateway(
            "logs",
            "logs <service> [--lines N]",
            "Show recent journal entries for a service.",
            static _ => ShellAccessRequirement.Authenticated,
            accessDescription: "Requires login."),
        CliCommandDefinition.Gateway(
            "runtime",
            "runtime [containers]|package-cache",
            "Inspect local Podman container state, or load live package-proxy cache stats.",
            ResolveRuntimeAccess,
            ["containers"],
            accessDescription: "Containers require login. Package cache is admin only."),
        CliCommandDefinition.Gateway(
            "agents",
            "agents list|show|create|edit|delete",
            "Inspect agents and manage user-owned agents via Gateway.",
            ResolveAgentsAccess,
            accessDescription: "List and show require login. Create, edit, and delete follow agent ownership rules."),
        CliCommandDefinition.Gateway(
            "chat",
            "chat start|resume|sessions|show-session",
            "Start or resume session-backed Narad chat runs for an agent.",
            static _ => ShellAccessRequirement.Authenticated,
            accessDescription: "Requires login."),
        CliCommandDefinition.Gateway(
            "autopilot",
            "autopilot schedules|runs|inbox",
            "Inspect scheduled autopilot activity, recent runs, and notification inbox state.",
            static _ => ShellAccessRequirement.Authenticated,
            accessDescription: "Requires login."),
        CliCommandDefinition.Gateway(
            "memories",
            "memories show <agentId>",
            "Inspect durable facts and session history for an agent.",
            static _ => ShellAccessRequirement.Authenticated,
            accessDescription: "Requires login."),
        CliCommandDefinition.Gateway(
            "providers",
            "providers list|types|create|edit|delete",
            "List providers, show provider types, create or edit provider profiles, or delete a provider via Gateway.",
            static _ => ShellAccessRequirement.Admin),
        CliCommandDefinition.Gateway(
            "models",
            "models list|create|edit|discover|probe|delete",
            "List models, create or edit model definitions, discover provider models, probe capabilities, or delete a model via Gateway.",
            ResolveModelsAccess,
            accessDescription: "List requires login. Create, edit, discover, probe, and delete are admin only."),
        CliCommandDefinition.Gateway(
            "connectors",
            "connectors list|show|delete",
            "Inspect or delete connectors via Gateway.",
            static _ => ShellAccessRequirement.Authenticated,
            accessDescription: "Requires login. Visibility and delete permissions still follow connector ownership and scope."),
        CliCommandDefinition.Gateway(
            "organization",
            "organization show|edit",
            "View or edit organization defaults used across agents.",
            ResolveOrganizationAccess,
            accessDescription: "Show requires login. Edit is admin only."),
        CliCommandDefinition.Gateway(
            "advanced",
            "advanced show|edit-system|edit-provider-path|edit-memory|show-persona|edit-persona",
            "Inspect and update advanced system, provider path policy, memory settings, and persona definitions.",
            ResolveAdvancedAccess,
            accessDescription: "Persona definitions show requires login. All other advanced actions are admin only."),
        CliCommandDefinition.Gateway(
            "users",
            "users list|invite|activate|deactivate|set-role|set-imports",
            "Inspect users and apply basic admin changes via Gateway.",
            static _ => ShellAccessRequirement.Admin),
    ];

    public static TuiApplication CreateDefault()
    {
        ShellCommandRunner commandRunner = new();
        SystemServiceInspector serviceInspector = new(commandRunner);
        RuntimeInspector runtimeInspector = new();

        WhoAmICommand whoAmICommand = new();
        StatusCommand statusCommand = new(serviceInspector, runtimeInspector);
        ServicesCommand servicesCommand = new(serviceInspector);
        LogsCommand logsCommand = new(commandRunner);
        RuntimeCommand runtimeCommand = new(runtimeInspector);
        AgentsCommand agentsCommand = new();
        AutopilotCommand autopilotCommand = new();
        AdvancedSettingsCommand advancedSettingsCommand = new();
        OrganizationCommand organizationCommand = new();
        ProvidersCommand providersCommand = new();
        ModelsCommand modelsCommand = new();
        ConnectorsCommand connectorsCommand = new();
        MemoriesCommand memoriesCommand = new();
        UsersCommand usersCommand = new();

        return new TuiApplication(
            new TuiShell(
                whoAmICommand,
                statusCommand,
                servicesCommand,
                logsCommand,
                runtimeCommand,
                agentsCommand,
                autopilotCommand,
                advancedSettingsCommand,
                organizationCommand,
                providersCommand,
                modelsCommand,
                connectorsCommand,
                memoriesCommand,
                usersCommand),
            whoAmICommand,
            statusCommand,
            servicesCommand,
            logsCommand,
            runtimeCommand,
            agentsCommand,
            autopilotCommand,
            advancedSettingsCommand,
            organizationCommand,
            providersCommand,
            modelsCommand,
            connectorsCommand,
            memoriesCommand,
            usersCommand);
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            string[] arguments = args;
            if (arguments.Length == 0)
            {
                return await shell.RunAsync(cancellationToken).ConfigureAwait(false);
            }

            string command = arguments[0];
            return await ExecuteAsync(command, arguments.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (ArgumentException exception)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return 1;
        }
        catch (InvalidOperationException exception)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return 1;
        }
        catch (IOException exception)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return 1;
        }
    }

    private async Task<int> ExecuteAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        string normalizedCommand = command.ToLowerInvariant();
        if (!TryResolveCommandDefinition(normalizedCommand, out CliCommandDefinition? commandDefinition)
            || commandDefinition is null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape($"Unknown command '{command}'. Use 'help' to see available commands.")}[/]");
            return 1;
        }

        TuiCommandOptions? gatewayOptions = null;
        IReadOnlyList<string> commandArguments = arguments;

        if (commandDefinition.Policy.UsesGateway)
        {
            gatewayOptions = TuiCommandOptions.Parse([.. arguments]);
            commandArguments = gatewayOptions.Positionals;

            ShellAccessRequirement requiredAccess = commandDefinition.Policy.ResolveAccess(commandArguments);
            Result<GatewayUserPayload> authenticationResult = await RequireAccessAsync(gatewayOptions, requiredAccess, cancellationToken).ConfigureAwait(false);
            if (!authenticationResult.IsSuccess)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(authenticationResult.ErrorMessage ?? "Authentication failed.")}[/]");
                return 1;
            }
        }

        Result result = commandDefinition.Name switch
        {
            "shell" => await RunShellAsync(cancellationToken).ConfigureAwait(false),
            "status" => await statusCommand.ExecuteAsync(null, cancellationToken).ConfigureAwait(false),
            "help" => RenderHelp(),
            "whoami" => await whoAmICommand.ExecuteAsync(gatewayOptions!, cancellationToken).ConfigureAwait(false),
            "services" => await servicesCommand.ExecuteAsync(cancellationToken).ConfigureAwait(false),
            "logs" => await logsCommand.ExecuteAsync(commandArguments, cancellationToken).ConfigureAwait(false),
            "runtime" => await runtimeCommand.ExecuteAsync(gatewayOptions, commandArguments, cancellationToken).ConfigureAwait(false),
            "agents" => await agentsCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            "chat" => await ChatCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            "autopilot" => await autopilotCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            "memories" => await memoriesCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            "advanced" => await advancedSettingsCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            "organization" => await organizationCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            "providers" => await providersCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            "models" => await modelsCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            "connectors" => await connectorsCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            "users" => await usersCommand.ExecuteAsync(gatewayOptions!, commandArguments, cancellationToken).ConfigureAwait(false),
            _ => Results.Failure($"Unknown command '{command}'. Use 'help' to see available commands."),
        };

        if (result.IsSuccess)
        {
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.ErrorMessage ?? "Command failed.")}[/]");
        return 1;
    }

    private static async Task<Result<GatewayUserPayload>> RequireAccessAsync(TuiCommandOptions options, ShellAccessRequirement requiredAccess, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<GatewayUserPayload>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayUserPayload> meResult = await client.GetMeAsync(cancellationToken).ConfigureAwait(false);
        if (!meResult.IsSuccess || meResult.Value is null)
        {
            return Results.Failure<GatewayUserPayload>(meResult.ErrorMessage ?? "Authentication failed.");
        }

        return ShellAccessEvaluator.HasAccess(requiredAccess, meResult.Value)
            ? meResult
            : Results.Failure<GatewayUserPayload>(ShellAccessEvaluator.GetAccessDeniedMessage(requiredAccess));
    }

    private static Result RenderHelp()
    {
        Table table = StripedTable.CreateRounded();
        table.AddColumn("Command");
        table.AddColumn("Purpose");
        foreach (CliCommandDefinition command in commandDefinitions)
        {
            table.AddStripedRow(command.HelpCommand, command.HelpText);
        }

        AnsiConsole.MarkupLine("[bold]Kautuka TUI[/]");
        AnsiConsole.MarkupLine("[yellow]Experimental[/]. The supported operator surface is the web UI.");
        AnsiConsole.MarkupLine("SSH-first operator commands for appliance inspection and admin control.");
        AnsiConsole.MarkupLine("Running without arguments launches the interactive shell.");
        AnsiConsole.MarkupLine("Login is presented first in the shell to unlock admin actions.");
        AnsiConsole.MarkupLine("Status can be used without login. Logs, containers, agents, chat, and direct CLI actions that require appliance context now authenticate before they run.");
        AnsiConsole.MarkupLine("Authenticated commands accept --token, --username, and --password, and prompt interactively when credentials are omitted.");
        AnsiConsole.MarkupLine($"Gateway URL resolves to {Markup.Escape(TuiCommandOptions.ResolveGatewayUrl().ToString())}.");
        AnsiConsole.MarkupLine("The TUI defaults to the canonical local Gateway port and can read config.json when present.");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        return Results.Success();
    }

    private async Task<Result> RunShellAsync(CancellationToken cancellationToken)
    {
        int exitCode = await shell.RunAsync(cancellationToken).ConfigureAwait(false);
        return exitCode == 0
            ? Results.Success()
            : Results.Failure($"Shell exited with status {exitCode}.");
    }

    private static bool TryResolveCommandDefinition(string normalizedCommand, out CliCommandDefinition? definition)
    {
        definition = commandDefinitions.FirstOrDefault(command => command.Matches(normalizedCommand));
        return definition is not null;
    }

    private static ShellAccessRequirement ResolveRuntimeAccess(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return ShellAccessRequirement.Authenticated;
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "package-cache" or "pkgs-cache" => ShellAccessRequirement.Admin,
            _ => ShellAccessRequirement.Authenticated,
        };
    }

    private static ShellAccessRequirement ResolveModelsAccess(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return ShellAccessRequirement.Authenticated;
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "list" => ShellAccessRequirement.Authenticated,
            "create" or "edit" or "discover" or "probe" or "delete" => ShellAccessRequirement.Admin,
            _ => ShellAccessRequirement.Authenticated,
        };
    }

    private static ShellAccessRequirement ResolveAgentsAccess(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return ShellAccessRequirement.Authenticated;
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "list" or "show" or "create" or "edit" or "delete" => ShellAccessRequirement.Authenticated,
            _ => ShellAccessRequirement.Authenticated,
        };
    }

    private static ShellAccessRequirement ResolveOrganizationAccess(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return ShellAccessRequirement.Authenticated;
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "show" => ShellAccessRequirement.Authenticated,
            "edit" => ShellAccessRequirement.Admin,
            _ => ShellAccessRequirement.Authenticated,
        };
    }

    private static ShellAccessRequirement ResolveAdvancedAccess(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return ShellAccessRequirement.Admin;
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "show-persona" => ShellAccessRequirement.Authenticated,
            "show" or "edit-system" or "edit-provider-path" or "edit-memory" or "edit-persona" => ShellAccessRequirement.Admin,
            _ => ShellAccessRequirement.Admin,
        };
    }

    private sealed record CliCommandPolicy(bool UsesGateway, Func<IReadOnlyList<string>, ShellAccessRequirement> ResolveAccess)
    {
        public static CliCommandPolicy Public()
            => new(false, static _ => ShellAccessRequirement.Public);

        public static CliCommandPolicy Gateway(Func<IReadOnlyList<string>, ShellAccessRequirement> resolveAccess)
            => new(true, resolveAccess);
    }

    private sealed record CliCommandDefinition(
        string Name,
        string HelpCommand,
        string HelpText,
        CliCommandPolicy Policy,
        IReadOnlyList<string> Aliases)
    {
        public bool Matches(string normalizedCommand)
            => string.Equals(Name, normalizedCommand, StringComparison.Ordinal)
                || Aliases.Any(alias => string.Equals(alias, normalizedCommand, StringComparison.Ordinal));

        public static CliCommandDefinition Public(
            string name,
            string helpCommand,
            string purpose,
            IReadOnlyList<string>? aliases = null)
            => new(
                name,
                helpCommand,
                purpose,
                CliCommandPolicy.Public(),
                aliases ?? []);

        public static CliCommandDefinition Gateway(
            string name,
            string helpCommand,
            string purpose,
            Func<IReadOnlyList<string>, ShellAccessRequirement> resolveAccess,
            IReadOnlyList<string>? aliases = null,
            string? accessDescription = null)
            => new(
                name,
                helpCommand,
                accessDescription is null ? purpose : $"{accessDescription} {purpose}",
                CliCommandPolicy.Gateway(resolveAccess),
                aliases ?? []);
    }
}
