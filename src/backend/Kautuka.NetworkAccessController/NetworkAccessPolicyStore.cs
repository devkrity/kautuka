using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kautuka.NetworkAccessController;

public sealed class NetworkAccessPolicyStore
{
    public const int MaxPendingRequests = 500;
    public const int MaxRules = 10000;
    private static readonly TimeSpan resolvedRequestRetention = TimeSpan.FromDays(30);

    private readonly object gate = new();
    private readonly string path;
    private readonly Action<string> syncDirectory;
    private readonly Action<string>? beforeRenameHook;
    private readonly IReadOnlyList<NetworkAccessRule> seedRules;
    private readonly HashSet<string> knownAdminUsers = new(StringComparer.Ordinal);
    private NetworkAccessPolicyState? state;
    private long diskWriteCount;

    public NetworkAccessPolicyStore(string path, IReadOnlyList<NetworkAccessRule>? seedRules = null)
        : this(path, FlushDirectory, beforeRenameHook: null, seedRules)
    {
    }

    internal NetworkAccessPolicyStore(
        string path,
        Action<string> syncDirectory,
        Action<string>? beforeRenameHook = null,
        IReadOnlyList<NetworkAccessRule>? seedRules = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(syncDirectory);
        this.path = Path.GetFullPath(path);
        this.syncDirectory = syncDirectory;
        this.beforeRenameHook = beforeRenameHook;
        this.seedRules = seedRules ?? NetworkAccessDefaultSeeds.DefaultRules;
    }

    public long DiskWriteCount => Interlocked.Read(ref diskWriteCount);

    public IReadOnlyList<NetworkAccessRule> SeedRules => seedRules;

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

    /// <summary>
    /// Validates and loads the policy state on startup. Throws if <c>state.json</c> is malformed (C12).
    /// </summary>
    public void EnsureLoaded()
    {
        lock (gate)
        {
            _ = Load();
        }
    }

    public void RegisterAdminUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        lock (gate)
        {
            knownAdminUsers.Add(userId.Trim());
        }
    }

    public NetworkAccessPolicyState GetRawStateSnapshot()
    {
        lock (gate)
        {
            return Load();
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
            if (actor.IsAdmin)
            {
                knownAdminUsers.Add(actor.UserId);
            }

            if (targetIsAdmin)
            {
                knownAdminUsers.Add(effectiveUserId);
            }

            NetworkAccessPolicyState current = Load();
            return ResolveUserPrivilegesUnsafe(current, effectiveUserId, isTargetAdmin: targetIsAdmin || (actor.IsAdmin && string.Equals(effectiveUserId, actor.UserId, StringComparison.Ordinal)));
        }
    }

    public UserPrivileges SetUserPrivileges(
        NetworkAccessActor actor,
        string targetUserId,
        bool canApproveOwn,
        bool canAllowAllAgents)
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
            knownAdminUsers.Add(actor.UserId);
            NetworkAccessPolicyState current = Load();
            var updated = new UserPrivileges(
                normalizedTarget,
                canApproveOwn,
                canAllowAllAgents,
                actor.UserId,
                DateTimeOffset.UtcNow);

            UserPrivileges[] nextPrivs =
            [
                .. current.UserPrivileges.Where(p => !string.Equals(p.UserId, normalizedTarget, StringComparison.Ordinal)),
                updated,
            ];

            Save(current with
            {
                UserPrivileges = nextPrivs,
            });
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
            UserPrivileges privs = ResolveUserPrivilegesUnsafe(current, userId, isTargetAdmin: false);
            return privs.CanApproveOwn && privs.CanAllowAllAgents;
        }
    }

    public BrakeState GetBrake(NetworkAccessActor actor)
    {
        ValidateActor(actor);
        lock (gate)
        {
            if (actor.IsAdmin)
            {
                knownAdminUsers.Add(actor.UserId);
            }

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
            if (actor.IsAdmin)
            {
                knownAdminUsers.Add(actor.UserId);
            }

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
                    if (existing is not null && existing.LockedByAdmin && !actor.IsAdmin)
                    {
                        throw new UnauthorizedAccessException("brake_locked: Only an administrator can release a brake set by an administrator.");
                    }

                    UserBrakeEntry[] users =
                    [
                        .. current.Brake.Users.Where(u => !string.Equals(u.UserId, effectiveUser, StringComparison.Ordinal)),
                    ];
                    nextBrake = current.Brake with { Users = users };
                }
            }

            // Per C21(c): Engaging and releasing the brake touches no rules and leaves rule revision unchanged.
            Save(current with { Brake = nextBrake });
            return nextBrake;
        }
    }

    public (long Revision, IReadOnlyList<NetworkAccessRequestView> Requests, UserPrivileges CallerPrivileges, BrakeState Brake) ListRequests(
        NetworkAccessActor actor,
        long? sinceRevision = null)
    {
        _ = sinceRevision;
        ValidateActor(actor);
        lock (gate)
        {
            if (actor.IsAdmin)
            {
                knownAdminUsers.Add(actor.UserId);
            }

            NetworkAccessPolicyState current = Load();
            UserPrivileges callerPrivs = ResolveUserPrivilegesUnsafe(current, actor.UserId, isTargetAdmin: actor.IsAdmin);

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
            if (actor.IsAdmin)
            {
                knownAdminUsers.Add(actor.UserId);
            }

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
            if (actor.IsAdmin)
            {
                knownAdminUsers.Add(actor.UserId);
            }

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

                UserPrivileges ownerPrivs = ResolveUserPrivilegesUnsafe(current, request.UserId, isTargetAdmin: actor.IsAdmin && string.Equals(actor.UserId, request.UserId, StringComparison.Ordinal));
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

                effectiveScope = string.IsNullOrWhiteSpace(scope) ? "agent" : scope.Trim();
                if (!allow && string.IsNullOrWhiteSpace(scope))
                {
                    effectiveScope = "agent";
                }

                if (effectiveScope is not ("agent" or "user"))
                {
                    throw new ArgumentException($"scope_not_allowed: Scope '{effectiveScope}' is not allowed for an ordinary agent request.", nameof(scope));
                }

                UserPrivileges ownerPrivs = ResolveUserPrivilegesUnsafe(
                    current,
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
            foreach (NetworkAccessRequest item in current.Requests)
            {
                if (string.Equals(item.Id, request.Id, StringComparison.Ordinal))
                {
                    updatedTargetRequest = item with { Status = newStatus, LastSeenUtc = now };
                    nextRequests.Add(updatedTargetRequest);
                }
                else if (item.Status == "pending" && ShouldResolveMatchingPendingRequest(rule, item))
                {
                    nextRequests.Add(item with { Status = newStatus, LastSeenUtc = now });
                }
                else
                {
                    nextRequests.Add(item);
                }
            }

            long nextRevision = checked(current.Revision + 1);
            Save(current with
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
            if (actor.IsAdmin)
            {
                knownAdminUsers.Add(actor.UserId);
            }

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

                        UserPrivileges privs = ResolveUserPrivilegesUnsafe(current, userId, isTargetAdmin: false);
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

                        if (spec.ExecContext is "agent-sandbox" or "skill" || spec.Attribution == "none")
                        {
                            throw new ArgumentException("scope_not_enforceable: any-public cannot be granted to AgentSandbox connectors, skills, or unattributed shared containers.", nameof(spec));
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
                            current,
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

            string newStatus = spec.Allowed ? "allowed" : "blocked";
            NetworkAccessRequest[] nextRequests =
            [
                .. current.Requests.Select(req =>
                    req.Status == "pending" && ShouldResolveMatchingPendingRequest(rule, req)
                        ? req with { Status = newStatus, LastSeenUtc = now }
                        : req),
            ];

            long nextRevision = checked(current.Revision + 1);
            Save(current with
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
            if (actor.IsAdmin)
            {
                knownAdminUsers.Add(actor.UserId);
            }

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
            Save(current with
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
                // Retries of an existing pending request increment attempts in memory without disk writes (U2).
                long nextAttempts = existing.Attempts == long.MaxValue ? long.MaxValue : existing.Attempts + 1;
                DateTimeOffset lastSeen = now > existing.LastSeenUtc ? now : existing.LastSeenUtc;
                var updatedPending = existing with
                {
                    Attempts = nextAttempts,
                    LastSeenUtc = lastSeen,
                    CompatProfile = compatProfile ?? existing.CompatProfile,
                };
                state = current with
                {
                    Requests = [.. current.Requests.Select(r => string.Equals(r.Id, id, StringComparison.Ordinal) ? updatedPending : r)],
                };
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
            Save(current with
            {
                Revision = existing is null ? current.Revision : checked(current.Revision + 1),
                Requests = activeRequests,
            });
            return (id, Overflow: false);
        }
    }

    private static bool ShouldResolveMatchingPendingRequest(NetworkAccessRule rule, NetworkAccessRequest req)
    {
        bool destMatches = rule.DestKind == "any-public"
            || (string.Equals(rule.DestKind, req.DestKind, StringComparison.Ordinal)
                && string.Equals(rule.Destination, req.Destination, StringComparison.Ordinal)
                && rule.Port == req.Port);
        if (!destMatches)
        {
            return false;
        }

        return rule.ScopeKind switch
        {
            "connector-system" => string.Equals(rule.ConnectorId, req.ConnectorId, StringComparison.Ordinal),
            "user" when !string.IsNullOrEmpty(rule.ConnectorId) =>
                string.Equals(rule.UserId, req.UserId, StringComparison.Ordinal)
                && string.Equals(rule.ConnectorId, req.ConnectorId, StringComparison.Ordinal),
            "user" =>
                !req.Systemwide
                && string.Equals(rule.UserId, req.UserId, StringComparison.Ordinal),
            "agent" =>
                !req.Systemwide
                && string.Equals(rule.UserId, req.UserId, StringComparison.Ordinal)
                && string.Equals(rule.AgentId, req.AgentId, StringComparison.Ordinal),
            _ => false,
        };
    }

    private UserPrivileges ResolveUserPrivilegesUnsafe(NetworkAccessPolicyState current, string userId, bool isTargetAdmin)
    {
        UserPrivileges? stored = current.UserPrivileges.FirstOrDefault(p => string.Equals(p.UserId, userId, StringComparison.Ordinal));
        if (stored is not null)
        {
            return stored;
        }

        bool isAdminDefault = isTargetAdmin || knownAdminUsers.Contains(userId);
        return new UserPrivileges(
            userId,
            CanApproveOwn: isAdminDefault,
            CanAllowAllAgents: isAdminDefault,
            SetBy: "system",
            SetAtUtc: DateTimeOffset.UnixEpoch);
    }

    private bool IsRulePausedUnsafe(NetworkAccessPolicyState current, NetworkAccessRule rule)
    {
        if (rule.ScopeKind == "user" && string.IsNullOrEmpty(rule.ConnectorId) && !string.IsNullOrEmpty(rule.UserId))
        {
            UserPrivileges privs = ResolveUserPrivilegesUnsafe(current, rule.UserId, isTargetAdmin: false);
            return !(privs.CanApproveOwn && privs.CanAllowAllAgents);
        }

        return false;
    }

    private NetworkAccessPolicyState Load()
    {
        if (state is not null)
        {
            return state;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return state = new NetworkAccessPolicyState(1, 0, [], [], [], BrakeState.Empty);
        }
        catch (DirectoryNotFoundException)
        {
            return state = new NetworkAccessPolicyState(1, 0, [], [], [], BrakeState.Empty);
        }

        NetworkAccessPolicyState loaded;
        try
        {
            loaded = JsonSerializer.Deserialize(bytes, NetworkAccessJsonContext.Default.NetworkAccessPolicyState)
                ?? throw new InvalidDataException("Network access policy state is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Network access policy state file contains malformed JSON.", ex);
        }

        if (loaded.SchemaVersion != 1
            || loaded.Revision < 0
            || loaded.Rules is null
            || loaded.Requests is null
            || loaded.Rules.Count > MaxRules
            || loaded.Requests.Count > MaxPendingRequests)
        {
            throw new InvalidDataException("Unsupported or out-of-bounds network access policy state.");
        }

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (NetworkAccessRule rule in loaded.Rules)
        {
            if (rule.ScopeKind is not ("agent" or "user" or "connector-system"))
            {
                throw new InvalidDataException($"Invalid rule scopeKind '{rule.ScopeKind}'.");
            }

            (string destKind, string normalizedDest, int port) = NetworkDestinationNormalizer.ValidateAndNormalizeRuleDestination(
                rule.DestKind,
                rule.Destination,
                rule.Port,
                rule.Allowed,
                rule.AllowLan,
                isAdmin: true);
            string expectedId = NetworkAccessRule.ComputeId(
                rule.ScopeKind,
                rule.UserId,
                rule.AgentId,
                rule.ConnectorId,
                destKind,
                normalizedDest,
                port);
            if (!string.Equals(rule.Id, expectedId, StringComparison.Ordinal) || !ruleIds.Add(rule.Id))
            {
                throw new InvalidDataException($"Invalid or duplicate rule ID '{rule.Id}'.");
            }
        }

        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (NetworkAccessRequest req in loaded.Requests)
        {
            if (req.Status is not ("pending" or "allowed" or "blocked") || req.Attempts < 1 || !requestIds.Add(req.Id))
            {
                throw new InvalidDataException($"Invalid or duplicate request '{req.Id}'.");
            }
        }

        return state = loaded with
        {
            UserPrivileges = loaded.UserPrivileges ?? [],
            Brake = loaded.Brake ?? BrakeState.Empty,
        };
    }

    private void Save(NetworkAccessPolicyState next)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - resolvedRequestRetention;
        NetworkAccessRequest[] prunedRequests =
        [
            .. next.Requests.Where(r => r.Status == "pending" || r.LastSeenUtc >= cutoff),
        ];
        NetworkAccessPolicyState toSave = next with { Requests = prunedRequests };

        string directory = Path.GetDirectoryName(path)!;
        var created = new List<string>();
        for (string? parent = directory; parent is not null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
        {
            created.Add(parent);
        }

        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string temporary = Path.Combine(directory, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(file, toSave, NetworkAccessJsonContext.Default.NetworkAccessPolicyState);
                file.Flush(flushToDisk: true);
            }

            beforeRenameHook?.Invoke(temporary);

            File.Move(temporary, path, overwrite: true);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            syncDirectory(directory);
            foreach (string parent in created)
            {
                string? ancestor = Path.GetDirectoryName(parent);
                if (ancestor is not null)
                {
                    syncDirectory(ancestor);
                }
            }

            state = toSave;
            Interlocked.Increment(ref diskWriteCount);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int handle = open(directory, 0x10000 | 0x80000); // O_RDONLY | O_DIRECTORY | O_CLOEXEC
        if (handle < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open network policy directory for durability.");
        }

        try
        {
            if (fsync(handle) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not persist network policy directory.");
            }
        }
        finally
        {
            _ = close(handle);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101", Justification = "Called only on Linux; libc open requires an explicitly marshaled UTF-8 pathname.")]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int handle);

    private static void ValidateActor(NetworkAccessActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateIdentifier(actor.UserId, nameof(actor));
    }

    private static void ValidateIdentifier(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
        {
            throw new ArgumentException("Identifier must be a non-empty bounded string without control characters.", paramName);
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
