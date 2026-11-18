namespace Kautuka.NetworkAccessController;

/// <summary>
/// Pure authorization and scope-resolution checks for policy store operations.
/// Enforces actor permissions, privilege requirements, and scope constraints
/// independently from state transitions and persistence.
/// </summary>
internal static class NetworkAccessPolicyAuthorizer
{
    internal delegate UserPrivileges ResolvePrivilegesCallback(string userId, bool isTargetAdmin);

    internal sealed record ResolvedDecisionTarget(
        string EffectiveScope,
        string? RuleUserId,
        string? RuleAgentId,
        string? RuleConnectorId);

    internal sealed record NormalizedRuleTarget(
        string ScopeKind,
        string? UserId,
        string? AgentId,
        string? ConnectorId);

    internal static void ValidateActor(NetworkAccessActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new ArgumentException("Actor userId cannot be empty.", nameof(actor));
        }
    }

    internal static void ValidateIdentifier(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Identifier '{paramName}' cannot be empty.", paramName);
        }

        if (string.Equals(value.Trim(), "-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Placeholder '-' is not a valid identifier for '{paramName}'.", paramName);
        }
    }

    internal static string AuthorizeGetUserPrivileges(NetworkAccessActor actor, string? targetUserId)
    {
        ValidateActor(actor);
        string effectiveUserId = string.IsNullOrWhiteSpace(targetUserId) ? actor.UserId : targetUserId.Trim();
        if (!actor.IsAdmin && !string.Equals(effectiveUserId, actor.UserId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("PermissionDenied: Cannot read another user's privileges.");
        }

        return effectiveUserId;
    }

    internal static string AuthorizeSetUserPrivileges(NetworkAccessActor actor, string targetUserId)
    {
        ValidateActor(actor);
        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may set user privileges.");
        }

        ValidateIdentifier(targetUserId, nameof(targetUserId));
        return targetUserId.Trim();
    }

    internal static string? AuthorizeSetBrake(
        NetworkAccessActor actor,
        string scope,
        string? targetUserId,
        BrakeState currentBrake)
    {
        ValidateActor(actor);
        if (string.IsNullOrWhiteSpace(scope) || scope is not ("user" or "system"))
        {
            throw new ArgumentException("invalid_scope: Brake scope must be 'user' or 'system'.", nameof(scope));
        }

        if (scope == "system")
        {
            if (!actor.IsAdmin)
            {
                throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may change the system brake.");
            }

            return null;
        }

        string effectiveUser = string.IsNullOrWhiteSpace(targetUserId) ? actor.UserId : targetUserId.Trim();
        if (!actor.IsAdmin && !string.Equals(effectiveUser, actor.UserId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("PermissionDenied: Cannot change another user's brake.");
        }

        UserBrakeEntry? existing = currentBrake.GetUserBrake(effectiveUser);
        if (existing is not null && existing.LockedByAdmin && !actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("brake_locked: Only an administrator can release or modify a brake locked by an administrator.");
        }

        return effectiveUser;
    }

    internal static string? AuthorizeListRules(NetworkAccessActor actor, string? filterUserId)
    {
        ValidateActor(actor);
        string? normalizedFilter = string.IsNullOrWhiteSpace(filterUserId) ? null : filterUserId.Trim();
        if (!actor.IsAdmin && normalizedFilter is not null && !string.Equals(normalizedFilter, actor.UserId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("PermissionDenied: Cannot list another user's rules.");
        }

        return normalizedFilter;
    }

    internal static bool CanViewRequest(NetworkAccessActor actor, NetworkAccessRequest req)
    {
        if (actor.IsAdmin)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(req.UserId)
            && req.UserId != "system"
            && string.Equals(req.UserId, actor.UserId, StringComparison.Ordinal);
    }

    internal static void AuthorizeDecideProbeRequest(
        NetworkAccessActor actor,
        NetworkAccessRequest request,
        bool allow,
        bool allowLan,
        ResolvePrivilegesCallback resolvePrivileges)
    {
        if (allow && allowLan && !actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may grant LAN access.");
        }

        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            if (actor.IsAdmin) return;
            throw new InvalidOperationException("User probe request is missing userId.");
        }

        if (!actor.IsAdmin && !string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("PermissionDenied: Cannot decide another user's probe request.");
        }

        if (!actor.IsAdmin && !resolvePrivileges(request.UserId, false).CanApproveOwn)
            throw new UnauthorizedAccessException("privilege_required: PermissionDenied: User cannot manage their own network access.");

    }

    internal static bool CanViewRule(NetworkAccessActor actor, NetworkAccessRule rule, string? normalizedFilter)
    {
        if (!actor.IsAdmin)
        {
            return string.Equals(rule.UserId, actor.UserId, StringComparison.Ordinal);
        }

        return normalizedFilter is null || string.Equals(rule.UserId, normalizedFilter, StringComparison.Ordinal);
    }

    internal static ResolvedDecisionTarget AuthorizeAndResolveDecideRequest(
        NetworkAccessActor actor,
        NetworkAccessRequest request,
        string? scope,
        ResolvePrivilegesCallback resolvePrivileges)
    {
        if (string.Equals(request.Purpose, "probe", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("PermissionDenied: Probe requests require temporary authorization.");

        if (string.Equals(request.UserId, "system", StringComparison.Ordinal))
        {
            if (!actor.IsAdmin)
                throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may decide a system request.");
            return new("system", null, null, null);
        }

        if (string.IsNullOrWhiteSpace(request.UserId))
            throw new InvalidOperationException("Network request is missing userId.");
        if (!actor.IsAdmin && !string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("PermissionDenied: Cannot decide another user's request.");
        if (!actor.IsAdmin && !resolvePrivileges(request.UserId, false).CanApproveOwn)
            throw new UnauthorizedAccessException("privilege_required: PermissionDenied: User cannot manage their own network access.");

        string target = string.IsNullOrWhiteSpace(scope)
            ? (!string.IsNullOrWhiteSpace(request.AgentId) ? "agent" : !string.IsNullOrWhiteSpace(request.ConnectorId) ? "connector" : "user")
            : scope.Trim();
        if (target == "system" && !actor.IsAdmin)
            throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may create a platform rule.");
        return target switch
        {
            "agent" when !string.IsNullOrWhiteSpace(request.AgentId) => new("agent", request.UserId, request.AgentId, null),
            "connector" when !string.IsNullOrWhiteSpace(request.ConnectorId) => new("user", request.UserId, null, request.ConnectorId),
            "user" => new("user", request.UserId, null, null),
            "system" when actor.IsAdmin => new("system", null, null, null),
            _ => throw new ArgumentException($"scope_not_allowed: Scope '{target}' is not valid for this request.", nameof(scope)),
        };
    }
    internal static NormalizedRuleTarget AuthorizeSetRule(
        NetworkAccessActor actor,
        RuleWriteSpec spec,
        ResolvePrivilegesCallback resolvePrivileges)
    {
        string scopeKind = spec.ScopeKind?.Trim() ?? string.Empty;
        string? userId = NormalizeOptional(spec.UserId);
        string? agentId = NormalizeOptional(spec.AgentId);
        string? connectorId = NormalizeOptional(spec.ConnectorId);

        if (!actor.IsAdmin && (scopeKind is "agent" or "user"))
        {
            if (!string.Equals(actor.UserId, userId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("PermissionDenied: Cannot manage another user's network rule.");
            if (!resolvePrivileges(actor.UserId, false).CanApproveOwn)
                throw new UnauthorizedAccessException("privilege_required: PermissionDenied: User cannot manage their own network access.");
        }

        switch (scopeKind)
        {
            case "agent":
                if (string.Equals(spec.DestKind, "any-public", StringComparison.Ordinal))
                {
                    throw new ArgumentException("scope_not_allowed: any-public rules cannot be written at 'agent' scope.", nameof(spec));
                }

                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(agentId) || !string.IsNullOrEmpty(connectorId))
                {
                    throw new ArgumentException("scope_not_allowed: 'agent' scope requires userId and agentId without connectorId.", nameof(spec));
                }

                if (userId == "system")
                {
                    if (!actor.IsAdmin)
                    {
                        throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may write system rules.");
                    }
                }
                else if (!actor.IsAdmin && !string.Equals(actor.UserId, userId, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("PermissionDenied: Cannot write rules for another user.");

                break;

            case "user":
                if (string.IsNullOrEmpty(userId) || !string.IsNullOrEmpty(agentId))
                {
                    throw new ArgumentException("scope_not_allowed: 'user' scope requires userId and no agentId.", nameof(spec));
                }

                if (!actor.IsAdmin && !string.Equals(actor.UserId, userId, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("PermissionDenied: Cannot write rules for another user.");

                if (string.Equals(spec.DestKind, "any-public", StringComparison.Ordinal))
                {
                    if (!actor.IsAdmin)
                    {
                        throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may grant any-public access.");
                    }

                    if (string.IsNullOrEmpty(connectorId))
                    {
                        throw new ArgumentException("scope_not_allowed: any-public rule requires a connectorId.", nameof(spec));
                    }

                    if (spec.ExecContext is "agent-sandbox" or "skill" or "skills" or "app" || spec.Attribution == "none")
                    {
                        throw new ArgumentException("scope_not_enforceable: any-public cannot be granted to AgentSandbox connectors, skills, app context, or unattributed shared containers.", nameof(spec));
                    }
                }
                else if (!string.IsNullOrEmpty(connectorId))
                {
                    if (spec.Attribution == "none")
                    {
                        throw new ArgumentException("scope_not_enforceable: User-scoped rule cannot be enforced on a shared unattributed connector.", nameof(spec));
                    }
                }
                break;

            case "connector-system":
                throw new ArgumentException("scope_not_allowed: connector-system rules are no longer supported.", nameof(spec));

            case "system":
                if (!actor.IsAdmin)
                {
                    throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may write systemwide rules.");
                }

                if (!string.IsNullOrEmpty(connectorId) || !string.IsNullOrEmpty(userId) || !string.IsNullOrEmpty(agentId))
                {
                    throw new ArgumentException("scope_not_allowed: 'system' scope must not specify userId, agentId, or connectorId.", nameof(spec));
                }

                if (string.Equals(spec.DestKind, "any-public", StringComparison.Ordinal))
                {
                    throw new ArgumentException("scope_not_allowed: any-public rules cannot be written at 'system' scope.", nameof(spec));
                }

                break;

            default:
                throw new ArgumentException($"scope_not_allowed: Unsupported scopeKind '{scopeKind}'.", nameof(spec));
        }

        return new(scopeKind, userId, agentId, connectorId);
    }

    internal static void AuthorizeDeleteRule(NetworkAccessActor actor, NetworkAccessRule existing, ResolvePrivilegesCallback resolvePrivileges)
    {
        if (string.Equals(existing.Source, "seed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("seed_rule_immutable: Built-in seed rules cannot be deleted.");
        }

        if (!actor.IsAdmin)
        {
            if (existing.DecidedByAdmin)
                throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may remove this protected rule.");
            if (!string.Equals(existing.UserId, actor.UserId, StringComparison.Ordinal)
                || existing.ScopeKind is not ("agent" or "user")
                || !resolvePrivileges(actor.UserId, false).CanApproveOwn)
            {
                throw new UnauthorizedAccessException("PermissionDenied: Cannot delete this rule.");
            }
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
