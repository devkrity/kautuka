namespace Kautuka.NetworkAccessController;

public sealed partial class NetworkAccessPolicyStore
{
    public long Revision
    {
        get
        {
            lock (gate)
            {
                return Load().Revision;
            }
        }
    }

    public void RegisterAdminUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        string trimmed = userId.Trim();
        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            if (!current.UserPrivileges.Any(p => string.Equals(p.UserId, trimmed, StringComparison.Ordinal)))
            {
                var adminRecord = new UserPrivileges(trimmed, CanApproveOwn: true, CanAllowAllAgents: true, SetBy: "system", SetAtUtc: DateTimeOffset.UtcNow);
                UserPrivileges[] nextPrivs = [.. current.UserPrivileges, adminRecord];
                SaveSync(current with { UserPrivileges = nextPrivs });
            }
        }
    }

    public UserPrivileges GetUserPrivileges(NetworkAccessActor actor, string? targetUserId = null, bool targetIsAdmin = false)
    {
        ValidateActor(actor);
        string effectiveUserId = string.IsNullOrWhiteSpace(targetUserId) ? actor.UserId : targetUserId.Trim();
        if (!actor.IsAdmin && !string.Equals(effectiveUserId, actor.UserId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("PermissionDenied: Cannot read another user's privileges.");
        }

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            bool isAdmin = targetIsAdmin || (actor.IsAdmin && string.Equals(effectiveUserId, actor.UserId, StringComparison.Ordinal));
            UserPrivileges privs = ResolveUserPrivilegesUnsafe(ref current, effectiveUserId, isTargetAdmin: isAdmin, persistAdminDefault: isAdmin);
            return privs;
        }
    }

    public UserPrivileges SetUserPrivileges(
        NetworkAccessActor actor,
        string targetUserId,
        bool canApproveOwn,
        bool canAllowAllAgents,
        bool? targetIsAdmin = null)
    {
        ValidateActor(actor);
        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may set user privileges.");
        }

        ValidateIdentifier(targetUserId, nameof(targetUserId));
        string normalizedTarget = targetUserId.Trim();

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            bool effectiveApprove = canApproveOwn;
            bool effectiveAllowAll = canAllowAllAgents;

            if (targetIsAdmin == true)
            {
                effectiveApprove = true;
                effectiveAllowAll = true;
            }

            var updated = new UserPrivileges(
                normalizedTarget,
                effectiveApprove,
                effectiveAllowAll,
                actor.UserId,
                DateTimeOffset.UtcNow);

            UserPrivileges[] nextPrivs =
            [
                .. current.UserPrivileges.Where(p => !string.Equals(p.UserId, normalizedTarget, StringComparison.Ordinal)),
                updated,
            ];

            SaveSync(current with
            {
                UserPrivileges = nextPrivs,
            });

            auditRing?.RecordPrivilegeChange(actor.UserId, normalizedTarget, effectiveApprove, effectiveAllowAll);

            return updated;
        }
    }

    public bool HasBothPrivileges(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            UserPrivileges privs = ResolveUserPrivilegesUnsafe(ref current, userId, isTargetAdmin: false);
            return privs.CanApproveOwn && privs.CanAllowAllAgents;
        }
    }

    public BrakeState GetBrake(NetworkAccessActor actor)
    {
        ValidateActor(actor);
        lock (gate)
        {
            return Load().Brake;
        }
    }

    public BrakeState GetBrakeSnapshot()
    {
        lock (gate)
        {
            return Load().Brake;
        }
    }

    public BrakeState SetBrake(
        NetworkAccessActor actor,
        string scope,
        string? targetUserId,
        bool on)
    {
        ValidateActor(actor);
        if (string.IsNullOrWhiteSpace(scope) || scope is not ("user" or "system"))
        {
            throw new ArgumentException("invalid_scope: Brake scope must be 'user' or 'system'.", nameof(scope));
        }

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            BrakeState nextBrake;

            if (scope == "system")
            {
                if (!actor.IsAdmin)
                {
                    throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may change the system brake.");
                }

                nextBrake = current.Brake with
                {
                    System = on,
                    SystemSetBy = on ? actor.UserId : null,
                    SystemSetAtUtc = on ? now : null,
                };
            }
            else
            {
                string effectiveUser = string.IsNullOrWhiteSpace(targetUserId) ? actor.UserId : targetUserId.Trim();
                if (!actor.IsAdmin && !string.Equals(effectiveUser, actor.UserId, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("PermissionDenied: Cannot change another user's brake.");
                }

                UserBrakeEntry? existing = current.Brake.GetUserBrake(effectiveUser);
                if (existing is not null && existing.LockedByAdmin && !actor.IsAdmin)
                {
                    throw new UnauthorizedAccessException("brake_locked: Only an administrator can release or modify a brake locked by an administrator.");
                }

                if (on)
                {
                    bool lockedByAdmin = actor.IsAdmin && !string.Equals(effectiveUser, actor.UserId, StringComparison.Ordinal);
                    if (actor.IsAdmin && string.Equals(effectiveUser, actor.UserId, StringComparison.Ordinal))
                    {
                        lockedByAdmin = false;
                    }

                    var entry = new UserBrakeEntry(effectiveUser, actor.UserId, now, lockedByAdmin);
                    UserBrakeEntry[] users =
                    [
                        .. current.Brake.Users.Where(u => !string.Equals(u.UserId, effectiveUser, StringComparison.Ordinal)),
                        entry,
                    ];
                    nextBrake = current.Brake with { Users = users };
                }
                else
                {
                    UserBrakeEntry[] users =
                    [
                        .. current.Brake.Users.Where(u => !string.Equals(u.UserId, effectiveUser, StringComparison.Ordinal)),
                    ];
                    nextBrake = current.Brake with { Users = users };
                }
            }

            // Durability: brakes must remain synchronously fsynced before replying (v7-10)
            SaveSync(current with { Brake = nextBrake });
            auditRing?.RecordBrakeChange(actor.UserId, scope, targetUserId, on);
            return nextBrake;
        }
    }

    public (long Revision, IReadOnlyList<NetworkAccessRequestView> Requests, UserPrivileges CallerPrivileges, BrakeState Brake) ListRequests(
        NetworkAccessActor actor,
        long? sinceRevision = null)
    {
        ValidateActor(actor);
        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            UserPrivileges callerPrivs = ResolveUserPrivilegesUnsafe(ref current, actor.UserId, isTargetAdmin: actor.IsAdmin);

            if (sinceRevision.HasValue
                && sinceRevision.Value >= current.Revision
                && sinceRevision.Value >= lastRequestActivityWatermark)
            {
                return (current.Revision, Array.Empty<NetworkAccessRequestView>(), callerPrivs, current.Brake);
            }

            var views = new List<NetworkAccessRequestView>();
            foreach (NetworkAccessRequest req in current.Requests)
            {
                if (actor.IsAdmin)
                {
                    views.Add(new NetworkAccessRequestView(
                        req,
                        WaitingForAdmin: false,
                        CompatibilityMode: !string.IsNullOrEmpty(req.CompatProfile)));
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(req.UserId)
                        || req.UserId == "system"
                        || !string.Equals(req.UserId, actor.UserId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool waitingForAdmin = req.Systemwide || !callerPrivs.CanApproveOwn;
                    views.Add(new NetworkAccessRequestView(
                        req,
                        WaitingForAdmin: waitingForAdmin,
                        CompatibilityMode: !string.IsNullOrEmpty(req.CompatProfile)));
                }
            }

            return (current.Revision, views, callerPrivs, current.Brake);
        }
    }

    public (long Revision, IReadOnlyList<NetworkAccessRuleView> Rules) ListRules(
        NetworkAccessActor actor,
        string? filterUserId = null)
    {
        ValidateActor(actor);
        string? normalizedFilter = string.IsNullOrWhiteSpace(filterUserId) ? null : filterUserId.Trim();
        if (!actor.IsAdmin && normalizedFilter is not null && !string.Equals(normalizedFilter, actor.UserId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("PermissionDenied: Cannot list another user's rules.");
        }

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            var views = new List<NetworkAccessRuleView>();
            var overriddenIds = current.Rules.Select(static r => r.Id).ToHashSet(StringComparer.Ordinal);

            if (actor.IsAdmin && normalizedFilter is null)
            {
                foreach (NetworkAccessRule seed in seedRules)
                {
                    if (!overriddenIds.Contains(seed.Id))
                    {
                        views.Add(new NetworkAccessRuleView(seed, Paused: false, OpenInternetBadge: false));
                    }
                }
            }

            foreach (NetworkAccessRule rule in current.Rules)
            {
                if (!actor.IsAdmin)
                {
                    if (!string.Equals(rule.UserId, actor.UserId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                else if (normalizedFilter is not null)
                {
                    if (!string.Equals(rule.UserId, normalizedFilter, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                bool paused = IsRulePausedUnsafe(current, rule);
                bool openInternet = rule.Allowed && string.Equals(rule.DestKind, "any-public", StringComparison.Ordinal);
                views.Add(new NetworkAccessRuleView(rule, paused, openInternet));
            }

            return (current.Revision, views);
        }
    }

    public DecideRequestResult DecideRequest(
        NetworkAccessActor actor,
        string requestId,
        bool allow,
        string? scope,
        long expectedRevision,
        bool allowLan = false)
    {
        ValidateActor(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            NetworkAccessRequest request = current.Requests.FirstOrDefault(r => string.Equals(r.Id, requestId, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException("Request not found.");

            // Check authorization BEFORE revision conflict so unauthorized callers never probe revisions.
            string effectiveScope;
            string? ruleUserId;
            string? ruleAgentId;
            string? ruleConnectorId;

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

                effectiveScope = string.IsNullOrWhiteSpace(scope)
                    ? (request.Attribution == "user" && !allow ? "user" : "connector-system")
                    : scope.Trim();

                if (effectiveScope == "user")
                {
                    if (request.Attribution == "none" || string.IsNullOrWhiteSpace(request.UserId))
                    {
                        throw new ArgumentException("scope_not_enforceable: Cannot grant user-scoped rule on a shared unattributed systemwide connector.", nameof(scope));
                    }

                    ruleUserId = request.UserId;
                    ruleAgentId = null;
                    ruleConnectorId = request.ConnectorId;
                }
                else if (effectiveScope == "connector-system")
                {
                    ruleUserId = null;
                    ruleAgentId = null;
                    ruleConnectorId = request.ConnectorId;
                }
                else
                {
                    throw new ArgumentException($"scope_not_allowed: Scope '{effectiveScope}' is not valid for a systemwide connector request.", nameof(scope));
                }
            }
            else if (string.Equals(request.UserId, "system", StringComparison.Ordinal))
            {
                if (!actor.IsAdmin)
                {
                    throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may decide a system request.");
                }

                effectiveScope = string.IsNullOrWhiteSpace(scope) ? "agent" : scope.Trim();
                if (effectiveScope != "agent")
                {
                    throw new ArgumentException("scope_not_allowed: System requests only support 'agent' scope.", nameof(scope));
                }

                ruleUserId = "system";
                ruleAgentId = string.IsNullOrWhiteSpace(request.AgentId) ? "system" : request.AgentId;
                ruleConnectorId = null;
            }
            else if (request.Attribution == "user" && string.IsNullOrWhiteSpace(request.AgentId) && !string.IsNullOrWhiteSpace(request.ConnectorId))
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

                UserPrivileges ownerPrivs = ResolveUserPrivilegesUnsafe(ref current, request.UserId, isTargetAdmin: actor.IsAdmin && string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal));
                if (!actor.IsAdmin && !ownerPrivs.CanApproveOwn)
                {
                    throw new UnauthorizedAccessException("privilege_required: PermissionDenied: User does not hold canApproveOwn privilege.");
                }

                effectiveScope = string.IsNullOrWhiteSpace(scope) ? "user" : scope.Trim();
                if (effectiveScope is not ("user" or "connector-system"))
                {
                    throw new ArgumentException($"scope_not_allowed: Scope '{effectiveScope}' is not valid for a connector probe request.", nameof(scope));
                }

                if (effectiveScope == "connector-system" && !actor.IsAdmin)
                {
                    throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may grant connector-system scope.");
                }

                ruleUserId = effectiveScope == "user" ? request.UserId : null;
                ruleAgentId = null;
                ruleConnectorId = request.ConnectorId;
            }
            else
            {
                // Ordinary user + agent request
                if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.AgentId))
                {
                    throw new InvalidOperationException("Ordinary agent request is missing userId or agentId.");
                }

                if (!actor.IsAdmin && !string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("PermissionDenied: Cannot decide another user's request.");
                }

                // For ordinary request "Keep blocked" (allow == false), ALWAYS force scope = "agent" (do not let it widen to "user"!)
                if (!allow)
                {
                    effectiveScope = "agent";
                }
                else
                {
                    effectiveScope = string.IsNullOrWhiteSpace(scope) ? "agent" : scope.Trim();
                }

                if (effectiveScope is not ("agent" or "user"))
                {
                    throw new ArgumentException($"scope_not_allowed: Scope '{effectiveScope}' is not allowed for an ordinary agent request.", nameof(scope));
                }

                UserPrivileges ownerPrivs = ResolveUserPrivilegesUnsafe(
                    ref current,
                    request.UserId,
                    isTargetAdmin: actor.IsAdmin && string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal));

                if (!actor.IsAdmin && !ownerPrivs.CanApproveOwn)
                {
                    throw new UnauthorizedAccessException("privilege_required: PermissionDenied: User does not hold canApproveOwn privilege.");
                }

                if (effectiveScope == "user")
                {
                    if (!(ownerPrivs.CanApproveOwn && ownerPrivs.CanAllowAllAgents))
                    {
                        throw new UnauthorizedAccessException("privilege_required: User must hold both canApproveOwn and canAllowAllAgents for user-scope rules.");
                    }

                    ruleUserId = request.UserId;
                    ruleAgentId = null;
                    ruleConnectorId = null;
                }
                else
                {
                    ruleUserId = request.UserId;
                    ruleAgentId = request.AgentId;
                    ruleConnectorId = null;
                }
            }

            if (current.Revision != expectedRevision)
            {
                throw new InvalidOperationException("revision_conflict: Policy revision has changed.");
            }

            (string destKind, string normalizedDest, int port) = NetworkDestinationNormalizer.ValidateAndNormalizeRuleDestination(
                request.DestKind,
                request.Destination,
                request.Port,
                allow,
                allow && allowLan,
                actor.IsAdmin);

            string ruleId = NetworkAccessRule.ComputeId(
                effectiveScope,
                ruleUserId,
                ruleAgentId,
                ruleConnectorId,
                destKind,
                normalizedDest,
                port);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var rule = new NetworkAccessRule(
                ruleId,
                effectiveScope,
                ruleUserId,
                ruleAgentId,
                ruleConnectorId,
                normalizedDest,
                destKind,
                port,
                allow,
                allow && allowLan && actor.IsAdmin,
                actor.UserId,
                now,
                Source: "user");

            NetworkAccessRule[] nextRules =
            [
                .. current.Rules.Where(r => !string.Equals(r.Id, ruleId, StringComparison.Ordinal)),
                rule,
            ];
            if (nextRules.Length > MaxRules)
            {
                throw new InvalidOperationException("rule_limit_reached: Maximum rule count exceeded.");
            }

            string newStatus = allow ? "allowed" : "blocked";
            NetworkAccessRequest? updatedTargetRequest = null;
            var nextRequests = new List<NetworkAccessRequest>(current.Requests.Count);

            var currentWithNewRule = current with { Rules = nextRules };

            foreach (NetworkAccessRequest item in current.Requests)
            {
                if (string.Equals(item.Id, request.Id, StringComparison.Ordinal))
                {
                    updatedTargetRequest = item with { Status = newStatus, LastSeenUtc = now };
                    nextRequests.Add(updatedTargetRequest);
                }
                else if (item.Status == "pending" && ShouldResolveMatchingPendingRequest(currentWithNewRule, item))
                {
                    // Only mark matching pending requests as resolved if they now evaluate to Allow! (v7-9)
                    nextRequests.Add(item with { Status = "allowed", LastSeenUtc = now });
                }
                else
                {
                    nextRequests.Add(item);
                }
            }

            long nextRevision = checked(current.Revision + 1);
            SaveSync(current with
            {
                Revision = nextRevision,
                Rules = nextRules,
                Requests = nextRequests,
            });

            return new DecideRequestResult(
                nextRevision,
                rule,
                updatedTargetRequest!,
                Paused: IsRulePausedUnsafe(state!, rule),
                OpenInternetBadge: rule.Allowed && rule.DestKind == "any-public");
        }
    }

    public (long Revision, NetworkAccessRuleView Rule) SetRule(
        NetworkAccessActor actor,
        RuleWriteSpec spec,
        long expectedRevision)
    {
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(spec);

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            string scopeKind = spec.ScopeKind?.Trim() ?? string.Empty;
            string? userId = NormalizeOptional(spec.UserId);
            string? agentId = NormalizeOptional(spec.AgentId);
            string? connectorId = NormalizeOptional(spec.ConnectorId);

            // Validate scope and caller permissions BEFORE revision conflict check.
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

                        UserPrivileges privs = ResolveUserPrivilegesUnsafe(ref current, userId, isTargetAdmin: false);
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

                        UserPrivileges ownerPrivs = ResolveUserPrivilegesUnsafe(
                            ref current,
                            userId,
                            isTargetAdmin: actor.IsAdmin && string.Equals(actor.UserId, userId, StringComparison.Ordinal));
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

                default:
                    throw new ArgumentException($"scope_not_allowed: Unsupported scopeKind '{scopeKind}'.", nameof(spec));
            }

            (string destKind, string normalizedDest, int port) = NetworkDestinationNormalizer.ValidateAndNormalizeRuleDestination(
                spec.DestKind,
                spec.Destination,
                spec.Port,
                spec.Allowed,
                spec.Allowed && spec.AllowLan,
                actor.IsAdmin);

            if (current.Revision != expectedRevision)
            {
                throw new InvalidOperationException("revision_conflict: Policy revision has changed.");
            }

            string ruleId = NetworkAccessRule.ComputeId(
                scopeKind,
                userId,
                agentId,
                connectorId,
                destKind,
                normalizedDest,
                port);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var rule = new NetworkAccessRule(
                ruleId,
                scopeKind,
                userId,
                agentId,
                connectorId,
                normalizedDest,
                destKind,
                port,
                spec.Allowed,
                spec.Allowed && spec.AllowLan && actor.IsAdmin,
                actor.UserId,
                now,
                Source: "user");

            NetworkAccessRule[] nextRules =
            [
                .. current.Rules.Where(r => !string.Equals(r.Id, ruleId, StringComparison.Ordinal)),
                rule,
            ];
            if (nextRules.Length > MaxRules)
            {
                throw new InvalidOperationException("rule_limit_reached: Maximum rule count exceeded.");
            }

            var currentWithNewRule = current with { Rules = nextRules };
            NetworkAccessRequest[] nextRequests =
            [
                .. current.Requests.Select(req =>
                    req.Status == "pending" && ShouldResolveMatchingPendingRequest(currentWithNewRule, req)
                        ? req with { Status = "allowed", LastSeenUtc = now }
                        : req),
            ];

            long nextRevision = checked(current.Revision + 1);
            SaveSync(current with
            {
                Revision = nextRevision,
                Rules = nextRules,
                Requests = nextRequests,
            });

            var view = new NetworkAccessRuleView(
                rule,
                Paused: IsRulePausedUnsafe(state!, rule),
                OpenInternetBadge: rule.Allowed && rule.DestKind == "any-public");
            return (nextRevision, view);
        }
    }

    public long DeleteRule(NetworkAccessActor actor, string ruleId, long expectedRevision)
    {
        ValidateActor(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            NetworkAccessRule? existing = current.Rules.FirstOrDefault(r => string.Equals(r.Id, ruleId, StringComparison.Ordinal));
            if (existing is null)
            {
                if (seedRules.Any(s => string.Equals(s.Id, ruleId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("seed_rule_immutable: Built-in seed rules cannot be deleted.");
                }

                throw new KeyNotFoundException("Rule not found.");
            }

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

            if (current.Revision != expectedRevision)
            {
                throw new InvalidOperationException("revision_conflict: Policy revision has changed.");
            }

            NetworkAccessRule[] nextRules =
            [
                .. current.Rules.Where(r => !string.Equals(r.Id, ruleId, StringComparison.Ordinal)),
            ];

            long nextRevision = checked(current.Revision + 1);
            SaveSync(current with
            {
                Revision = nextRevision,
                Rules = nextRules,
            });
            return nextRevision;
        }
    }

    public void IncrementBlockedRequestAttemptInMemory(
        string? userId,
        string? agentId,
        string? connectorId,
        bool systemwide,
        string destKind,
        string destination,
        int port)
    {
        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool modified = false;
            var updated = new List<NetworkAccessRequest>(current.Requests.Count);
            foreach (NetworkAccessRequest req in current.Requests)
            {
                if (string.Equals(req.DestKind, destKind, StringComparison.Ordinal)
                    && string.Equals(req.Destination, destination, StringComparison.Ordinal)
                    && req.Port == port
                    && string.Equals(req.UserId, userId, StringComparison.Ordinal)
                    && string.Equals(req.AgentId, agentId, StringComparison.Ordinal)
                    && string.Equals(req.ConnectorId, connectorId, StringComparison.Ordinal)
                    && req.Systemwide == systemwide)
                {
                    long nextAttempts = req.Attempts == long.MaxValue ? long.MaxValue : req.Attempts + 1;
                    updated.Add(req with { Attempts = nextAttempts, LastSeenUtc = now });
                    modified = true;
                }
                else
                {
                    updated.Add(req);
                }
            }

            if (modified)
            {
                state = current with { Requests = updated };
            }
        }
    }

    public (string? RequestId, bool Overflow) RecordOrUpdatePendingRequest(
        string? userId,
        string? agentId,
        string? connectorId,
        bool systemwide,
        string attribution,
        string destKind,
        string destination,
        int port,
        string actorKind,
        string origin,
        string? purpose = null,
        string? compatProfile = null,
        DateTimeOffset? observedAtUtc = null)
    {
        DateTimeOffset now = observedAtUtc ?? DateTimeOffset.UtcNow;
        string id = NetworkAccessRequest.ComputeId(
            actorKind,
            attribution,
            userId,
            agentId,
            connectorId,
            systemwide,
            origin,
            destKind,
            destination,
            port);

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            NetworkAccessRequest? existing = current.Requests.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
            if (existing is { Status: "pending" })
            {
                // Retries of an existing pending request increment attempts in memory without synchronous disk writes.
                long nextAttempts = existing.Attempts == long.MaxValue ? long.MaxValue : existing.Attempts + 1;
                DateTimeOffset lastSeen = now > existing.LastSeenUtc ? now : existing.LastSeenUtc;
                var updatedPending = existing with
                {
                    Attempts = nextAttempts,
                    LastSeenUtc = lastSeen,
                    CompatProfile = compatProfile ?? existing.CompatProfile,
                };
                var nextRequests = current.Requests.Select(r => string.Equals(r.Id, id, StringComparison.Ordinal) ? updatedPending : r).ToList();
                state = current with { Requests = nextRequests };
                lastRequestActivityWatermark = Math.Max(lastRequestActivityWatermark, checked(current.Revision + 1));
                return (id, Overflow: false);
            }

            DateTimeOffset cutoff = now - resolvedRequestRetention;
            var activeRequests = current.Requests
                .Where(r => !string.Equals(r.Id, id, StringComparison.Ordinal) && (r.Status == "pending" || r.LastSeenUtc >= cutoff))
                .ToList();

            if (activeRequests.Count >= MaxPendingRequests)
            {
                NetworkAccessRequest? oldestResolved = activeRequests
                    .Where(static r => r.Status != "pending")
                    .MinBy(static r => r.LastSeenUtc);
                if (oldestResolved is not null)
                {
                    activeRequests.Remove(oldestResolved);
                }
                else
                {
                    return (null, Overflow: true);
                }
            }

            long attempts = existing is null ? 1 : (existing.Attempts == long.MaxValue ? long.MaxValue : existing.Attempts + 1);
            var newRequest = new NetworkAccessRequest(
                id,
                userId,
                agentId,
                connectorId,
                systemwide,
                attribution,
                destination,
                destKind,
                port,
                actorKind,
                existing?.FirstSeenUtc ?? now,
                now,
                attempts,
                Status: "pending",
                origin,
                purpose,
                compatProfile);

            activeRequests.Add(newRequest);

            NetworkAccessPolicyState nextState = current with
            {
                Requests = activeRequests,
            };

            EnqueuePendingRequestSave(nextState);
            lastRequestActivityWatermark = Math.Max(lastRequestActivityWatermark, checked(current.Revision + 1));

            return (id, Overflow: false);
        }
    }

    private bool ShouldResolveMatchingPendingRequest(NetworkAccessPolicyState stateWithNewRule, NetworkAccessRequest req)
    {
        return EvaluatesToAllow(stateWithNewRule, req);
    }

    private static bool EvaluatesToAllow(NetworkAccessPolicyState state, NetworkAccessRequest req)
    {
        if (req.Systemwide)
        {
            if (req.Attribution == "none")
            {
                NetworkAccessRule? sysRule = FindExactRule(
                    state.Rules,
                    scopeKind: "connector-system",
                    userId: null,
                    agentId: null,
                    connectorId: req.ConnectorId,
                    req.DestKind,
                    req.Destination,
                    req.Port);
                return sysRule is { Allowed: true };
            }

            if (req.Attribution == "user")
            {
                NetworkAccessRule? userRule = FindExactRule(
                    state.Rules,
                    scopeKind: "user",
                    userId: req.UserId,
                    agentId: null,
                    connectorId: req.ConnectorId,
                    req.DestKind,
                    req.Destination,
                    req.Port);
                if (userRule is not null)
                {
                    return userRule.Allowed;
                }

                NetworkAccessRule? anyPublic = FindAnyPublicRule(state.Rules, req.UserId, req.ConnectorId);
                if (anyPublic is { Allowed: true })
                {
                    if (req.DestKind == "ip" && NetworkAddressClassifier.IsNonPublicV4(req.Destination))
                    {
                        return false;
                    }
                    return true;
                }

                NetworkAccessRule? sysRule = FindExactRule(
                    state.Rules,
                    scopeKind: "connector-system",
                    userId: null,
                    agentId: null,
                    connectorId: req.ConnectorId,
                    req.DestKind,
                    req.Destination,
                    req.Port);
                return sysRule is { Allowed: true };
            }

            return false;
        }

        if (string.Equals(req.UserId, "system", StringComparison.Ordinal))
        {
            NetworkAccessRule? sysRule = state.Rules.FirstOrDefault(r =>
                r.ScopeKind == "agent"
                && r.UserId == "system"
                && (string.IsNullOrEmpty(req.AgentId) || r.AgentId == req.AgentId)
                && string.Equals(r.DestKind, req.DestKind, StringComparison.Ordinal)
                && string.Equals(r.Destination, req.Destination, StringComparison.Ordinal)
                && r.Port == req.Port);
            return sysRule is { Allowed: true };
        }

        if (req.Attribution == "user" && string.IsNullOrEmpty(req.AgentId) && !string.IsNullOrEmpty(req.ConnectorId))
        {
            NetworkAccessRule? userRule = FindExactRule(
                state.Rules,
                scopeKind: "user",
                userId: req.UserId,
                agentId: null,
                connectorId: req.ConnectorId,
                req.DestKind,
                req.Destination,
                req.Port);
            if (userRule is not null)
            {
                return userRule.Allowed;
            }

            NetworkAccessRule? anyPublic = FindAnyPublicRule(state.Rules, req.UserId, req.ConnectorId);
            if (anyPublic is { Allowed: true })
            {
                if (req.DestKind == "ip" && NetworkAddressClassifier.IsNonPublicV4(req.Destination))
                {
                    return false;
                }
                return true;
            }

            NetworkAccessRule? sysRule = FindExactRule(
                state.Rules,
                scopeKind: "connector-system",
                userId: null,
                agentId: null,
                connectorId: req.ConnectorId,
                req.DestKind,
                req.Destination,
                req.Port);
            return sysRule is { Allowed: true };
        }

        // Ordinary agent request:
        NetworkAccessRule? agentRule = FindExactRule(
            state.Rules,
            scopeKind: "agent",
            userId: req.UserId,
            agentId: req.AgentId,
            connectorId: null,
            req.DestKind,
            req.Destination,
            req.Port);
        if (agentRule is not null)
        {
            return agentRule.Allowed;
        }

        if (!string.IsNullOrEmpty(req.UserId) && HasBothPrivilegesUnsafe(state, req.UserId))
        {
            NetworkAccessRule? userRule = FindExactRule(
                state.Rules,
                scopeKind: "user",
                userId: req.UserId,
                agentId: null,
                connectorId: null,
                req.DestKind,
                req.Destination,
                req.Port);
            if (userRule is not null)
            {
                return userRule.Allowed;
            }
        }

        if (!string.IsNullOrEmpty(req.UserId) && !string.IsNullOrEmpty(req.ConnectorId))
        {
            NetworkAccessRule? anyPublic = FindAnyPublicRule(state.Rules, req.UserId, req.ConnectorId);
            if (anyPublic is { Allowed: true })
            {
                if (req.DestKind == "ip" && NetworkAddressClassifier.IsNonPublicV4(req.Destination))
                {
                    return false;
                }
                return true;
            }
        }

        return false;
    }

    private static NetworkAccessRule? FindExactRule(
        IEnumerable<NetworkAccessRule> rules,
        string scopeKind,
        string? userId,
        string? agentId,
        string? connectorId,
        string destKind,
        string destination,
        int port)
    {
        return rules.FirstOrDefault(r =>
            string.Equals(r.ScopeKind, scopeKind, StringComparison.Ordinal)
            && string.Equals(r.UserId, userId, StringComparison.Ordinal)
            && string.Equals(r.AgentId, agentId, StringComparison.Ordinal)
            && string.Equals(r.ConnectorId, connectorId, StringComparison.Ordinal)
            && string.Equals(r.DestKind, destKind, StringComparison.Ordinal)
            && string.Equals(r.Destination, destination, StringComparison.Ordinal)
            && r.Port == port);
    }

    private static NetworkAccessRule? FindAnyPublicRule(
        IEnumerable<NetworkAccessRule> rules,
        string? userId,
        string? connectorId)
    {
        return rules.FirstOrDefault(r =>
            string.Equals(r.ScopeKind, "user", StringComparison.Ordinal)
            && string.Equals(r.UserId, userId, StringComparison.Ordinal)
            && string.Equals(r.ConnectorId, connectorId, StringComparison.Ordinal)
            && string.Equals(r.DestKind, "any-public", StringComparison.Ordinal));
    }

    private static bool HasBothPrivilegesUnsafe(NetworkAccessPolicyState state, string userId)
    {
        UserPrivileges? privs = state.UserPrivileges.FirstOrDefault(p => string.Equals(p.UserId, userId, StringComparison.Ordinal));
        return privs is not null && privs.CanApproveOwn && privs.CanAllowAllAgents;
    }

    private UserPrivileges ResolveUserPrivilegesUnsafe(
        ref NetworkAccessPolicyState current,
        string userId,
        bool isTargetAdmin,
        bool persistAdminDefault = false)
    {
        UserPrivileges? stored = current.UserPrivileges.FirstOrDefault(p => string.Equals(p.UserId, userId, StringComparison.Ordinal));
        if (stored is not null)
        {
            return stored;
        }

        if (isTargetAdmin)
        {
            var adminDefault = new UserPrivileges(
                userId,
                CanApproveOwn: true,
                CanAllowAllAgents: true,
                SetBy: "system",
                SetAtUtc: DateTimeOffset.UtcNow);

            if (persistAdminDefault)
            {
                UserPrivileges[] nextPrivs = [.. current.UserPrivileges, adminDefault];
                current = current with { UserPrivileges = nextPrivs };
                SaveSync(current);
            }

            return adminDefault;
        }

        return new UserPrivileges(
            userId,
            CanApproveOwn: false,
            CanAllowAllAgents: false,
            SetBy: "system",
            SetAtUtc: DateTimeOffset.UnixEpoch);
    }

    private bool IsRulePausedUnsafe(NetworkAccessPolicyState current, NetworkAccessRule rule)
    {
        if (rule.ScopeKind == "user" && string.IsNullOrEmpty(rule.ConnectorId) && !string.IsNullOrEmpty(rule.UserId))
        {
            NetworkAccessPolicyState snapshot = current;
            UserPrivileges privs = ResolveUserPrivilegesUnsafe(ref snapshot, rule.UserId, isTargetAdmin: false);
            return !(privs.CanApproveOwn && privs.CanAllowAllAgents);
        }

        return false;
    }

    private static void ValidateActor(NetworkAccessActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new ArgumentException("Actor userId cannot be empty.", nameof(actor));
        }
    }

    private static void ValidateIdentifier(string value, string paramName)
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

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
