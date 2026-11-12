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

        if (req.Systemwide)
        {
            return false;
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

        if (request.Systemwide)
        {
            if (!actor.IsAdmin)
            {
                throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may decide a systemwide connector probe request.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            throw new InvalidOperationException("User probe request is missing userId.");
        }

        if (!actor.IsAdmin && !string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("PermissionDenied: Cannot decide another user's probe request.");
        }

        if (!actor.IsAdmin)
        {
            UserPrivileges ownerPrivs = resolvePrivileges(request.UserId, false);
            if (!ownerPrivs.CanApproveOwn)
            {
                throw new UnauthorizedAccessException("privilege_required: PermissionDenied: User does not hold canApproveOwn privilege.");
            }
        }
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
        bool allow,
        string? scope,
        ResolvePrivilegesCallback resolvePrivileges)
    {
        if (request.Systemwide)
        {
            if (!actor.IsAdmin)
            {
                throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may decide a systemwide connector request.");
            }

            if (allow && string.IsNullOrWhiteSpace(scope))
            {
                throw new ArgumentException("scope_required: Systemwide allow decisions require an explicit scope.", nameof(scope));
            }

            string effectiveScope = string.IsNullOrWhiteSpace(scope)
                ? (request.Attribution == "user" && !allow ? "user" : "connector-system")
                : scope.Trim();

            if (effectiveScope == "user")
            {
                if (request.Attribution == "none" || string.IsNullOrWhiteSpace(request.UserId))
                {
                    throw new ArgumentException("scope_not_enforceable: Cannot grant user-scoped rule on a shared unattributed systemwide connector.", nameof(scope));
                }

                return new(effectiveScope, request.UserId, null, request.ConnectorId);
            }

            if (effectiveScope == "connector-system")
            {
                return new(effectiveScope, null, null, request.ConnectorId);
            }

            throw new ArgumentException($"scope_not_allowed: Scope '{effectiveScope}' is not valid for a systemwide connector request.", nameof(scope));
        }

        if (string.Equals(request.UserId, "system", StringComparison.Ordinal))
        {
            if (!actor.IsAdmin)
            {
                throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may decide a system request.");
            }

            string effectiveScope = string.IsNullOrWhiteSpace(scope) ? "agent" : scope.Trim();
            if (effectiveScope != "agent")
            {
                throw new ArgumentException("scope_not_allowed: System requests only support 'agent' scope.", nameof(scope));
            }

            return new(effectiveScope, "system", string.IsNullOrWhiteSpace(request.AgentId) ? "system" : request.AgentId, null);
        }

        if (request.Attribution == "user" && string.IsNullOrWhiteSpace(request.AgentId) && !string.IsNullOrWhiteSpace(request.ConnectorId))
        {
            // User-attributed connector probe request (A1)
            if (string.IsNullOrWhiteSpace(request.UserId))
            {
                throw new InvalidOperationException("User-attributed request is missing userId.");
            }

            if (!actor.IsAdmin && !string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("PermissionDenied: Cannot decide another user's request.");
            }

            UserPrivileges ownerPrivs = resolvePrivileges(request.UserId, actor.IsAdmin && string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal));
            if (!actor.IsAdmin && !ownerPrivs.CanApproveOwn)
            {
                throw new UnauthorizedAccessException("privilege_required: PermissionDenied: User does not hold canApproveOwn privilege.");
            }

            string effectiveScope = string.IsNullOrWhiteSpace(scope) ? "user" : scope.Trim();
            if (effectiveScope is not ("user" or "connector-system"))
            {
                throw new ArgumentException($"scope_not_allowed: Scope '{effectiveScope}' is not valid for a connector probe request.", nameof(scope));
            }

            if (effectiveScope == "connector-system" && !actor.IsAdmin)
            {
                throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may grant connector-system scope.");
            }

            return new(effectiveScope, effectiveScope == "user" ? request.UserId : null, null, request.ConnectorId);
        }

        // Ordinary user + agent request
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new InvalidOperationException("Ordinary agent request is missing userId or agentId.");
        }

        if (!actor.IsAdmin && !string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("PermissionDenied: Cannot decide another user's request.");
        }

        // For ordinary request "Keep blocked" (allow == false), ALWAYS force scope = "agent".
        string ordScope = !allow
            ? "agent"
            : (string.IsNullOrWhiteSpace(scope) ? "agent" : scope.Trim());

        if (ordScope is not ("agent" or "user"))
        {
            throw new ArgumentException($"scope_not_allowed: Scope '{ordScope}' is not allowed for an ordinary agent request.", nameof(scope));
        }

        UserPrivileges ordPrivs = resolvePrivileges(
            request.UserId,
            actor.IsAdmin && string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal));

        if (!actor.IsAdmin && !ordPrivs.CanApproveOwn)
        {
            throw new UnauthorizedAccessException("privilege_required: PermissionDenied: User does not hold canApproveOwn privilege.");
        }

        if (ordScope == "user")
        {
            if (!(ordPrivs.CanApproveOwn && ordPrivs.CanAllowAllAgents))
            {
                throw new UnauthorizedAccessException("privilege_required: User must hold both canApproveOwn and canAllowAllAgents for user-scope rules.");
            }

            return new(ordScope, request.UserId, null, null);
        }

        return new(ordScope, request.UserId, request.AgentId, null);
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
                else if (!actor.IsAdmin)
                {
                    if (!string.Equals(actor.UserId, userId, StringComparison.Ordinal))
                    {
                        throw new UnauthorizedAccessException("PermissionDenied: Cannot write rules for another user.");
                    }

                    UserPrivileges privs = resolvePrivileges(userId, false);
                    if (!privs.CanApproveOwn)
                    {
                        throw new UnauthorizedAccessException("PermissionDenied: User does not hold canApproveOwn privilege.");
                    }
                }

                break;

            case "user":
                if (string.IsNullOrEmpty(userId) || !string.IsNullOrEmpty(agentId))
                {
                    throw new ArgumentException("scope_not_allowed: 'user' scope requires userId and no agentId.", nameof(spec));
                }

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
                    if (!actor.IsAdmin)
                    {
                        throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may write user-scoped connector rules.");
                    }

                    if (spec.Attribution == "none")
                    {
                        throw new ArgumentException("scope_not_enforceable: User-scoped rule cannot be enforced on a shared unattributed connector.", nameof(spec));
                    }
                }
                else
                {
                    // User scope without connectorId ('all my agents')
                    if (!actor.IsAdmin && !string.Equals(actor.UserId, userId, StringComparison.Ordinal))
                    {
                        throw new UnauthorizedAccessException("PermissionDenied: Cannot write user-scope rules for another user.");
                    }

                    UserPrivileges ownerPrivs = resolvePrivileges(
                        userId,
                        actor.IsAdmin && string.Equals(actor.UserId, userId, StringComparison.Ordinal));
                    if (!(ownerPrivs.CanApproveOwn && ownerPrivs.CanAllowAllAgents))
                    {
                        throw new UnauthorizedAccessException("privilege_required: Target user must hold both canApproveOwn and canAllowAllAgents.");
                    }
                }

                break;

            case "connector-system":
                if (!actor.IsAdmin)
                {
                    throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may write connector-system rules.");
                }

                if (string.IsNullOrEmpty(connectorId) || !string.IsNullOrEmpty(userId) || !string.IsNullOrEmpty(agentId))
                {
                    throw new ArgumentException("scope_not_allowed: 'connector-system' scope requires connectorId only.", nameof(spec));
                }

                if (string.Equals(spec.DestKind, "any-public", StringComparison.Ordinal))
                {
                    throw new ArgumentException("scope_not_allowed: any-public rules are only supported at 'user' scope with a connectorId.", nameof(spec));
                }

                break;

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

    internal static void AuthorizeDeleteRule(NetworkAccessActor actor, NetworkAccessRule existing)
    {
        if (string.Equals(existing.Source, "seed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("seed_rule_immutable: Built-in seed rules cannot be deleted.");
        }

        if (!actor.IsAdmin)
        {
            if (!string.Equals(existing.UserId, actor.UserId, StringComparison.Ordinal)
                || existing.ScopeKind is not ("agent" or "user")
                || !string.IsNullOrEmpty(existing.ConnectorId))
            {
                throw new UnauthorizedAccessException("PermissionDenied: Cannot delete this rule.");
            }
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
