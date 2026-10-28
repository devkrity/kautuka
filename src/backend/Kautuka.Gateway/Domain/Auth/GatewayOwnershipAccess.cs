namespace Kautuka.Gateway.Domain.Auth;

internal static class GatewayOwnershipAccess
{
    public static bool IsAdmin(GatewayUser? user)
        => user is not null && string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);

    public static bool IsSelf(GatewayUser? user, string? ownerUserId)
        => user is not null
            && !string.IsNullOrWhiteSpace(ownerUserId)
            && string.Equals(user.Id, ownerUserId, StringComparison.Ordinal);

    public static bool CanAccessAgent(GatewayUser? user, GatewayAgent? agent)
        => user is not null
            && agent is not null
            && (string.Equals(agent.Scope, GatewayOwnershipScopes.System, StringComparison.Ordinal)
                || IsSelf(user, agent.OwnerUserId));

    public static bool CanManageAgent(GatewayUser? user, GatewayAgent? agent)
        => user is not null
            && agent is not null
            && (string.Equals(agent.Scope, GatewayOwnershipScopes.System, StringComparison.Ordinal)
                ? IsAdmin(user)
                : IsSelf(user, agent.OwnerUserId));

    public static bool CanAccessConnector(GatewayUser? user, GatewayMcpServer? server)
        => user is not null
            && server is not null
            && (string.Equals(server.Scope, GatewayOwnershipScopes.System, StringComparison.Ordinal)
                || IsSelf(user, server.OwnerUserId));

    public static bool CallerCanUse(string? actingUserId, string? scope, string? ownerUserId)
        => string.Equals(scope, GatewayOwnershipScopes.System, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(actingUserId)
                && string.Equals(actingUserId, ownerUserId, StringComparison.Ordinal));

    public static bool CanManageConnector(GatewayUser? user, GatewayMcpServer? server)
        => user is not null
            && server is not null
            && (string.Equals(server.Scope, GatewayOwnershipScopes.System, StringComparison.Ordinal)
                ? IsAdmin(user)
                : IsSelf(user, server.OwnerUserId));

    public static bool CanAccessSkill(GatewayUser? user, GatewaySkill? skill)
        => user is not null
            && skill is not null
            && (string.Equals(skill.Scope, GatewayOwnershipScopes.System, StringComparison.Ordinal)
                || IsSelf(user, skill.OwnerUserId));

    public static bool CanManageSkill(GatewayUser? user, GatewaySkill? skill)
        => user is not null
            && skill is not null
            && (string.Equals(skill.Scope, GatewayOwnershipScopes.System, StringComparison.Ordinal)
                ? IsAdmin(user)
                : IsSelf(user, skill.OwnerUserId));

    public static bool CanAccessSession(GatewayUser? user, GatewaySession? session, bool adminFallback = false)
        => user is not null
            && session is not null
            && (IsSelf(user, session.OwnerUserId) || (adminFallback && IsAdmin(user)));

    public static bool CanAccessMemory(GatewayUser? user, GatewayMemoryFact? fact, bool adminFallback = false)
        => user is not null
            && fact is not null
            && (IsSelf(user, fact.OwnerUserId) || (adminFallback && IsAdmin(user)));

    public static bool CanAccessApproval(GatewayUser? user, GatewayToolApprovalRequest? request, bool adminFallback = false)
        => user is not null
            && request is not null
            && (IsSelf(user, request.UserId) || (adminFallback && IsAdmin(user)));

    public static string ResolveCatalogScope(GatewayUser user, string? requestedScope)
        => IsAdmin(user)
            && string.Equals(requestedScope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase)
            ? GatewayOwnershipScopes.User
            : IsAdmin(user)
                ? GatewayOwnershipScopes.System
                : GatewayOwnershipScopes.User;

    public static string? ResolveCatalogOwnerUserId(string scope, GatewayUser user)
        => string.Equals(scope, GatewayOwnershipScopes.User, StringComparison.Ordinal)
            ? user.Id
            : null;
}
