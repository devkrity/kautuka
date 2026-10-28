// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Users/UsersCommand.cs | Namespace: Kautuka.Tui.Features.Users
using System.Diagnostics.CodeAnalysis;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;

namespace Kautuka.Tui.Features.Users;

public sealed class UsersCommand
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result> ExecuteAsync(TuiCommandOptions options, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return await ListAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count is 4 or 5 && args[0].Equals("invite", StringComparison.OrdinalIgnoreCase))
        {
            string? email = args.Count == 5 ? args[4] : null;
            return await InviteAsync(options, args[1], args[2], args[3], email, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("activate", StringComparison.OrdinalIgnoreCase))
        {
            return await SetActiveAsync(options, args[1], true, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("deactivate", StringComparison.OrdinalIgnoreCase))
        {
            return await SetActiveAsync(options, args[1], false, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 3 && args[0].Equals("set-role", StringComparison.OrdinalIgnoreCase))
        {
            return await SetRoleAsync(options, args[1], args[2], cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 4 && args[0].Equals("set-imports", StringComparison.OrdinalIgnoreCase))
        {
            return await SetImportPermissionsAsync(options, args[1], args[2], args[3], cancellationToken).ConfigureAwait(false);
        }

        return Results.Failure("Usage: users list | users invite <username> <password> <admin|member> [email] | users activate <userId> | users deactivate <userId> | users set-role <userId> <admin|member> | users set-imports <userId> <true|false> <true|false>");
    }

    private static async Task<Result> ListAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayUsersResponse> usersResult = await client.GetUsersAsync(cancellationToken).ConfigureAwait(false);
        if (!usersResult.IsSuccess || usersResult.Value is null)
        {
            return Results.Failure(usersResult.ErrorMessage ?? "Failed to load users.");
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Id");
        table.AddColumn("Username");
        table.AddColumn("Role");
        table.AddColumn("Active");
        table.AddColumn("Connector Import");

        foreach (GatewayUserListItem user in usersResult.Value.Users)
        {
            table.AddStripedRow(
                user.Id,
                user.Username,
                user.Role,
                user.IsActive ? "yes" : "no",
                user.CanImportConnectors ? "yes" : "no");
        }

        AnsiConsole.Write(table);
        return Results.Success();
    }

    private static async Task<Result> InviteAsync(TuiCommandOptions options, string username, string password, string role, string? email, CancellationToken cancellationToken)
    {
        string normalizedRole = NormalizeRole(role);
        if (normalizedRole.Length == 0)
        {
            return Results.Failure("Role must be admin or member.");
        }

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayUserResponse> inviteResult = await client.InviteUserAsync(username, password, normalizedRole, string.IsNullOrWhiteSpace(email) ? null : email.Trim(), cancellationToken).ConfigureAwait(false);
        if (!inviteResult.IsSuccess || inviteResult.Value is null)
        {
            return Results.Failure(inviteResult.ErrorMessage ?? "Failed to invite user.");
        }

        AnsiConsole.MarkupLine($"[green]Invited user {Markup.Escape(inviteResult.Value.User.Username)} as {Markup.Escape(inviteResult.Value.User.Role)}.[/]");
        return Results.Success();
    }

    private static async Task<Result> SetRoleAsync(TuiCommandOptions options, string userId, string role, CancellationToken cancellationToken)
    {
        string normalizedRole = NormalizeRole(role);
        if (normalizedRole.Length == 0)
        {
            return Results.Failure("Role must be admin or member.");
        }

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayBooleanSuccessResponse> roleResult = await client.SetUserRoleAsync(userId, normalizedRole, cancellationToken).ConfigureAwait(false);
        if (!roleResult.IsSuccess)
        {
            return Results.Failure(roleResult.ErrorMessage ?? "Failed to update user role.");
        }

        AnsiConsole.MarkupLine($"[green]Updated role for {Markup.Escape(userId)} to {Markup.Escape(normalizedRole)}.[/]");
        return Results.Success();
    }

    private static async Task<Result> SetActiveAsync(TuiCommandOptions options, string userId, bool isActive, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayBooleanSuccessResponse> activeResult = await client.SetUserActiveAsync(userId, isActive, cancellationToken).ConfigureAwait(false);
        if (!activeResult.IsSuccess)
        {
            return Results.Failure(activeResult.ErrorMessage ?? "Failed to update user activity.");
        }

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(userId)} is now {(isActive ? "active" : "inactive")}.[/]");
        return Results.Success();
    }

    private static async Task<Result> SetImportPermissionsAsync(TuiCommandOptions options, string userId, string connectorsValue, string skillsValue, CancellationToken cancellationToken)
    {
        if (!TryParseBoolean(connectorsValue, out bool canImportConnectors)
            || !TryParseBoolean(skillsValue, out bool canImportSkills))
        {
            return Results.Failure("Import permission values must be true or false.");
        }

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayBooleanSuccessResponse> updateResult = await client.SetUserImportPermissionsAsync(userId, canImportConnectors, canImportSkills, cancellationToken).ConfigureAwait(false);
        if (!updateResult.IsSuccess)
        {
            return Results.Failure(updateResult.ErrorMessage ?? "Failed to update import permissions.");
        }

        AnsiConsole.MarkupLine($"[green]Updated import permissions for {Markup.Escape(userId)}.[/]");
        return Results.Success();
    }

    private static string NormalizeRole(string role)
        => role.Trim().ToLowerInvariant() switch
        {
            "admin" or "role_admin" => "admin",
            "member" or "user" or "role_member" => "user",
            _ => string.Empty,
        };

    private static bool TryParseBoolean(string value, out bool parsed)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true":
            case "yes":
            case "y":
            case "1":
            case "on":
                parsed = true;
                return true;
            case "false":
            case "no":
            case "n":
            case "0":
            case "off":
                parsed = false;
                return true;
            default:
                parsed = false;
                return false;
        }
    }
}
