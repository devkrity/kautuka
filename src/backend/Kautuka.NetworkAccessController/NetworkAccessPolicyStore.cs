namespace Kautuka.NetworkAccessController;

public sealed partial class NetworkAccessPolicyStore
{
    public NetworkAccessApprovalLimits GetApprovalLimits(NetworkAccessActor actor)
    {
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("PermissionDenied: Administrator privileges are required.");
        lock (gate) return ResolveApprovalLimits(Load());
    }

    public NetworkAccessApprovalLimits SetApprovalLimits(NetworkAccessActor actor, int global, int perUser)
    {
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("PermissionDenied: Administrator privileges are required.");
        if (global < 1 || perUser < 1 || perUser > global) throw new ArgumentException("Pending request limits must be positive, and the per-user limit cannot exceed the global limit.");
        var limits = new NetworkAccessApprovalLimits(global, perUser);
        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            SaveSync(current with { ApprovalLimits = limits, Revision = checked(current.Revision + 1) });
        }
        return limits;
    }

    private static NetworkAccessApprovalLimits ResolveApprovalLimits(NetworkAccessPolicyState current)
    {
        NetworkAccessApprovalLimits limits = current.ApprovalLimits ?? new(MaxPendingRequests, DefaultMaxPendingRequestsPerUser);
        if (limits.Global < 1 || limits.PerUser < 1 || limits.PerUser > limits.Global)
            throw new InvalidDataException("Network access pending request limits are invalid.");
        return limits;
    }

    private sealed record TemporaryProbeGrant(
        string ContainerId,
        string? UserId,
        string? ConnectorId,
        string DestKind,
        string Destination,
        int Port,
        bool Allowed,
        bool AllowLan);

    private sealed record TemporaryProbePendingEntry(
        string ContainerId,
        NetworkAccessRequest Request);

    // Ephemeral in-memory probe decisions and pending cards (v7-14); never persisted to disk.
    private readonly Dictionary<string, TemporaryProbePendingEntry> probeRequestsById = new(StringComparer.Ordinal);
    private readonly List<TemporaryProbeGrant> probeGrants = [];

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

    public UserPrivileges GetUserPrivileges(NetworkAccessActor actor, string? targetUserId = null, bool targetIsAdmin = false)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string effectiveUserId = NetworkAccessPolicyAuthorizer.AuthorizeGetUserPrivileges(actor, targetUserId);

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            bool isAdmin = targetIsAdmin || (actor.IsAdmin && string.Equals(effectiveUserId, actor.UserId, StringComparison.Ordinal));
            UserPrivileges privs = ResolveUserPrivilegesUnsafe(ref current, effectiveUserId, isTargetAdmin: isAdmin);
            return privs;
        }
    }

    public UserPrivileges SetUserPrivileges(
        NetworkAccessActor actor,
        string targetUserId,
        bool canApproveOwn,
        bool? targetIsAdmin = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(targetUserId);
        string normalizedTarget = NetworkAccessPolicyAuthorizer.AuthorizeSetUserPrivileges(actor, targetUserId);
        _ = targetIsAdmin; // Role defaults apply only when a record is absent; they never override an explicit saved value.

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            bool effectiveApprove = canApproveOwn;

            var updated = new UserPrivileges(
                normalizedTarget,
                effectiveApprove,
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

            auditRing?.RecordPrivilegeChange(actor.UserId, normalizedTarget, effectiveApprove);

            return updated;
        }
    }

    public bool CanManageOwnNetworkAccess(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            UserPrivileges privs = ResolveUserPrivilegesUnsafe(ref current, userId, isTargetAdmin: false);
            return privs.CanApproveOwn;
        }
    }

    public bool HasStoredUserPrivileges(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        lock (gate)
        {
            return Load().UserPrivileges.Any(privileges =>
                string.Equals(privileges.UserId, userId, StringComparison.Ordinal));
        }
    }

    public BrakeState GetBrake(NetworkAccessActor actor)
    {
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
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
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
        if (string.IsNullOrWhiteSpace(scope) || scope is not ("user" or "system"))
        {
            throw new ArgumentException("invalid_scope: Brake scope must be 'user' or 'system'.", nameof(scope));
        }

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            string? effectiveUser = NetworkAccessPolicyAuthorizer.AuthorizeSetBrake(actor, scope, targetUserId, current.Brake);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            BrakeState nextBrake;

            if (effectiveUser is null)
            {
                nextBrake = current.Brake with
                {
                    System = on,
                    SystemSetBy = on ? actor.UserId : null,
                    SystemSetAtUtc = on ? now : null,
                };
            }
            else
            {
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
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
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
                if (!NetworkAccessPolicyAuthorizer.CanViewRequest(actor, req))
                {
                    continue;
                }

                bool waitingForAdmin = !actor.IsAdmin && (string.IsNullOrWhiteSpace(req.UserId) || req.UserId == "system" || !callerPrivs.CanApproveOwn);
                views.Add(new NetworkAccessRequestView(
                    req,
                    WaitingForAdmin: waitingForAdmin,
                    CompatibilityMode: !string.IsNullOrEmpty(req.CompatProfile)));
            }

            foreach (TemporaryProbePendingEntry probeEntry in probeRequestsById.Values)
            {
                NetworkAccessRequest req = probeEntry.Request;
                if (!NetworkAccessPolicyAuthorizer.CanViewRequest(actor, req))
                {
                    continue;
                }

                bool waitingForAdmin = !actor.IsAdmin && (string.IsNullOrWhiteSpace(req.UserId) || req.UserId == "system" || !callerPrivs.CanApproveOwn);
                views.Add(new NetworkAccessRequestView(
                    req,
                    WaitingForAdmin: waitingForAdmin,
                    CompatibilityMode: !string.IsNullOrEmpty(req.CompatProfile)));
            }

            return (current.Revision, views, callerPrivs, current.Brake);
        }
    }

    public (long Revision, IReadOnlyList<NetworkAccessRuleView> Rules) ListRules(
        NetworkAccessActor actor,
        string? filterUserId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string? normalizedFilter = NetworkAccessPolicyAuthorizer.AuthorizeListRules(actor, filterUserId);

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
                if (!NetworkAccessPolicyAuthorizer.CanViewRule(actor, rule, normalizedFilter))
                {
                    continue;
                }

                bool paused = NetworkAccessRuleMatcher.IsPaused(current, rule);
                bool openInternet = rule.Allowed && string.Equals(rule.DestKind, "any-public", StringComparison.Ordinal);
                views.Add(new NetworkAccessRuleView(rule, paused, openInternet));
            }

            return (current.Revision, views);
        }
    }

    internal async Task<string?> WaitForPendingResolutionAsync(string requestId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (gate)
            {
                NetworkAccessPolicyState current = Load();
                NetworkAccessRequest? request = current.Requests.FirstOrDefault(r => r.Id == requestId);
                if (request is null) return null;
                if (current.Brake.System) return "brake_system";
                if (current.Brake.IsUserBraked(request.UserId)) return "brake_user";
                if (request.Status != "pending") return request.Status;
                changed = (policyChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            if (probeRequestsById.TryGetValue(requestId, out TemporaryProbePendingEntry? probeEntry))
            {
                return DecideProbeRequestUnsafe(actor, current, probeEntry, allow, expectedRevision, allowLan);
            }

            NetworkAccessRequest request = current.Requests.FirstOrDefault(r => string.Equals(r.Id, requestId, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException("Request not found.");

            // Check authorization BEFORE revision conflict so unauthorized callers never probe revisions.
            NetworkAccessPolicyAuthorizer.ResolvedDecisionTarget resolved = NetworkAccessPolicyAuthorizer.AuthorizeAndResolveDecideRequest(
                actor,
                request,
                scope,
                (userId, isTargetAdmin) => ResolveUserPrivilegesUnsafe(ref current, userId, isTargetAdmin));
            string effectiveScope = resolved.EffectiveScope;
            string? ruleUserId = resolved.RuleUserId;
            string? ruleAgentId = resolved.RuleAgentId;
            string? ruleConnectorId = resolved.RuleConnectorId;

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

            (ruleId, _) = ResolveProtectedRuleWrite(current, actor, ruleId, effectiveScope,
                ruleUserId, ruleAgentId, ruleConnectorId, destKind, normalizedDest, port, allow);

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
                Source: "user",
                DecidedByAdmin: actor.IsAdmin);

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
                    updatedTargetRequest = item with
                    {
                        Status = allow && !EvaluatesToAllow(currentWithNewRule, item) ? "pending" : newStatus,
                        LastSeenUtc = now,
                    };
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
                Paused: NetworkAccessRuleMatcher.IsPaused(state!, rule),
                OpenInternetBadge: rule.Allowed && rule.DestKind == "any-public");
        }
    }

    public (long Revision, string Status) EnsureProviderPermission(
        NetworkAccessActor actor, string destination, string destKind, int port, bool allowLan, long? confirmedRevision)
    {
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("Only an administrator can set provider permissions.");
        if (destKind is not ("host" or "ip")) throw new ArgumentException("Provider permissions require an exact destination.", nameof(destKind));

        (string kind, string host, int targetPort) = NetworkDestinationNormalizer.ValidateAndNormalizeRuleDestination(
            destKind, destination, port, allowed: true, allowLan, actor.IsAdmin, allowLoopback: allowLan);

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            IReadOnlyList<NetworkAccessRule> matching = NetworkAccessRuleMatcher.FindApplicable(current, null, null, null, kind, host, targetPort);
            if (current.Brake.System || matching.Any(rule => !rule.Allowed || (allowLan && !rule.AllowLan)))
                return (current.Revision, "restricted");
            if (matching.Count != 0 || seedRules.Any(rule => rule.ScopeKind == "system" && rule.Allowed
                && rule.DestKind == kind && rule.Destination == host && rule.Port == targetPort && (!allowLan || rule.AllowLan)))
                return (current.Revision, "allowed");
            if (confirmedRevision != current.Revision)
                return (current.Revision, "confirmation_required");

            // Keep the permission check and the existing rule writer under the same policy lock.
            (long revision, _) = SetRule(actor, new RuleWriteSpec("system", null, null, null, host, kind, targetPort, true, allowLan), current.Revision);
            return (revision, "allowed");
        }
    }

    public (long Revision, NetworkAccessRuleView Rule) SetRule(
        NetworkAccessActor actor,
        RuleWriteSpec spec,
        long expectedRevision,
        string? replaceRuleId = null)
    {
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(spec);

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            NetworkAccessRule? replaced = null;
            if (!string.IsNullOrWhiteSpace(replaceRuleId))
            {
                replaced = current.Rules.FirstOrDefault(rule => string.Equals(rule.Id, replaceRuleId, StringComparison.Ordinal))
                    ?? throw new KeyNotFoundException("Rule to replace was not found.");
            }
            NetworkAccessPolicyAuthorizer.NormalizedRuleTarget normalized = NetworkAccessPolicyAuthorizer.AuthorizeSetRule(
                actor,
                spec,
                (userId, isTargetAdmin) => ResolveUserPrivilegesUnsafe(ref current, userId, isTargetAdmin));
            string scopeKind = normalized.ScopeKind;
            string? userId = normalized.UserId;
            string? agentId = normalized.AgentId;
            string? connectorId = normalized.ConnectorId;

            (string destKind, string normalizedDest, int port) = NetworkDestinationNormalizer.ValidateAndNormalizeRuleDestination(
                spec.DestKind,
                spec.Destination,
                spec.Port,
                spec.Allowed,
                spec.Allowed && spec.AllowLan,
                actor.IsAdmin,
                allowLoopback: scopeKind == "system" && actor.IsAdmin && spec.Allowed && spec.AllowLan);

            string ruleId = NetworkAccessRule.ComputeId(
                scopeKind,
                userId,
                agentId,
                connectorId,
                destKind,
                normalizedDest,
                port);

            (ruleId, string? protectedRuleId) = ResolveProtectedRuleWrite(current, actor, ruleId, scopeKind,
                userId, agentId, connectorId, destKind, normalizedDest, port, spec.Allowed);
            bool preserveReplacedAdminRule = !spec.Allowed && protectedRuleId is not null && replaced is not null
                && string.Equals(replaced.Id, protectedRuleId, StringComparison.Ordinal);
            if (replaced is not null && !preserveReplacedAdminRule)
            {
                NetworkAccessPolicyAuthorizer.AuthorizeDeleteRule(actor, replaced,
                    (targetUserId, isTargetAdmin) => ResolveUserPrivilegesUnsafe(ref current, targetUserId, isTargetAdmin));
            }
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("revision_conflict: Policy revision has changed.");

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
                Source: "user",
                DecidedByAdmin: actor.IsAdmin);

            NetworkAccessRule[] nextRules =
            [
                .. current.Rules.Where(r => !string.Equals(r.Id, ruleId, StringComparison.Ordinal)
                    && (preserveReplacedAdminRule || !string.Equals(r.Id, replaceRuleId, StringComparison.Ordinal))),
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
                Paused: NetworkAccessRuleMatcher.IsPaused(state!, rule),
                OpenInternetBadge: rule.Allowed && rule.DestKind == "any-public");
            return (nextRevision, view);
        }
    }

    private static (string RuleId, string? ProtectedRuleId) ResolveProtectedRuleWrite(
        NetworkAccessPolicyState current, NetworkAccessActor actor, string ruleId, string scopeKind,
        string? userId, string? agentId, string? connectorId, string destKind, string destination, int port, bool allowed)
    {
        if (actor.IsAdmin)
            return (ruleId, null);
        string? normalizedConnector = connectorId is not null && userId is not null
            ? NetworkAccessRuleMatcher.ConnectorForUser(connectorId, userId) : connectorId;
        NetworkAccessRule? existing = current.Rules.FirstOrDefault(rule => rule.DecidedByAdmin
            && rule.ScopeKind == scopeKind && rule.UserId == userId && rule.AgentId == agentId
            && (rule.ConnectorId is not null && userId is not null
                ? NetworkAccessRuleMatcher.ConnectorForUser(rule.ConnectorId, userId) : rule.ConnectorId) == normalizedConnector
            && rule.DestKind == destKind && rule.Destination == destination && rule.Port == port);
        if (existing is null)
            return (ruleId, null);
        if (!existing.Allowed && allowed)
            throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may change this protected rule.");
        return (NetworkAccessIdGenerator.ComputeBase32Id("user-rule-over-admin|" + existing.Id), existing.Id);
    }

    public long DeleteRule(NetworkAccessActor actor, string ruleId, long expectedRevision)
    {
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
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

            NetworkAccessPolicyAuthorizer.AuthorizeDeleteRule(actor, existing,
                (userId, isTargetAdmin) => ResolveUserPrivilegesUnsafe(ref current, userId, isTargetAdmin));

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
                    && string.Equals(req.ConnectorId, connectorId, StringComparison.Ordinal))
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
                EnqueuePendingRequestSave(current with { Requests = nextRequests });
                lastRequestActivityWatermark = Math.Max(lastRequestActivityWatermark, checked(current.Revision + 1));
                return (id, Overflow: false);
            }

            DateTimeOffset cutoff = now - resolvedRequestRetention;
            var activeRequests = current.Requests
                .Where(r => !string.Equals(r.Id, id, StringComparison.Ordinal) && (r.Status == "pending" || r.LastSeenUtc >= cutoff))
                .ToList();

            NetworkAccessApprovalLimits limits = ResolveApprovalLimits(current);
            int pendingProbes = probeRequestsById.Values.Count(entry => entry.Request.Status == "pending");
            int userPendingProbes = probeRequestsById.Values.Count(entry => entry.Request.Status == "pending" && string.Equals(entry.Request.UserId, userId, StringComparison.Ordinal));
            if (activeRequests.Count(r => r.Status == "pending" && string.Equals(r.UserId, userId, StringComparison.Ordinal)) + userPendingProbes >= limits.PerUser
                || activeRequests.Count(r => r.Status == "pending") + pendingProbes >= limits.Global)
                return (null, Overflow: true);

            while (activeRequests.Count + pendingProbes >= limits.Global)
            {
                NetworkAccessRequest? oldestResolved = activeRequests
                    .Where(static r => r.Status != "pending")
                    .OrderBy(static r => r.LastSeenUtc)
                    .ThenBy(static r => r.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
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
        IReadOnlyList<NetworkAccessRule> matches = NetworkAccessRuleMatcher.FindApplicable(
            state, req.UserId, req.AgentId, req.ConnectorId, req.DestKind, req.Destination, req.Port,
            allowAnyPublic: req.Origin == "container" && req.ConnectorId is not null);
        return !matches.Any(static rule => !rule.Allowed)
            && matches.Any(rule => rule.Allowed && (req.DestKind != "ip"
                || !NetworkAddressClassifier.IsNonPublicV4(req.Destination) || rule.AllowLan
                || (req.Destination == NetworkAddressClassifier.PackageProxyServiceAddress && req.Port == NetworkAddressClassifier.PackageProxyServicePort)));
    }
    private UserPrivileges ResolveUserPrivilegesUnsafe(
        ref NetworkAccessPolicyState current,
        string userId,
        bool isTargetAdmin)
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
                SetBy: "system",
                SetAtUtc: DateTimeOffset.UtcNow);

            return adminDefault;
        }

        return new UserPrivileges(
            userId,
            CanApproveOwn: false,
            SetBy: "system",
            SetAtUtc: DateTimeOffset.UnixEpoch);
    }

    public bool TryEvaluateTemporaryProbeDecision(
        string containerId,
        string? userId,
        string? connectorId,
        string destKind,
        string destination,
        int port,
        out bool allowed,
        out bool allowLan)
    {
        allowed = false;
        allowLan = false;
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return false;
        }

        string? normalizedUser = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
        string? normalizedConnector = string.IsNullOrWhiteSpace(connectorId) ? null : connectorId.Trim();
        lock (gate)
        {
            TemporaryProbeGrant? grant = probeGrants.FirstOrDefault(g =>
                string.Equals(g.ContainerId, containerId, StringComparison.Ordinal)
                && string.Equals(g.UserId, normalizedUser, StringComparison.Ordinal)
                && string.Equals(g.ConnectorId, normalizedConnector, StringComparison.Ordinal)
                && string.Equals(g.DestKind, destKind, StringComparison.Ordinal)
                && string.Equals(g.Destination, destination, StringComparison.Ordinal)
                && g.Port == port);
            if (grant is null)
            {
                return false;
            }

            allowed = grant.Allowed;
            allowLan = grant.AllowLan;
            return true;
        }
    }

    public string? RecordOrUpdatePendingProbeRequest(
        string containerId,
        string? userId,
        string? agentId,
        string? connectorId,
        string attribution,
        string destKind,
        string destination,
        int port,
        string actorKind,
        string origin,
        string? compatProfile = null,
        DateTimeOffset? observedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        DateTimeOffset now = observedAtUtc ?? DateTimeOffset.UtcNow;
        string id = NetworkAccessIdGenerator.ComputeBase32Id(
            $"probe|{containerId}|{attribution}|{userId ?? string.Empty}|{connectorId ?? string.Empty}|{origin}|{destKind}|{destination}|{port}");

        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            if (probeRequestsById.TryGetValue(id, out TemporaryProbePendingEntry? existing))
            {
                long nextAttempts = existing.Request.Attempts == long.MaxValue ? long.MaxValue : existing.Request.Attempts + 1;
                DateTimeOffset lastSeen = now > existing.Request.LastSeenUtc ? now : existing.Request.LastSeenUtc;
                NetworkAccessRequest updated = existing.Request with
                {
                    Attempts = nextAttempts,
                    LastSeenUtc = lastSeen,
                    CompatProfile = compatProfile ?? existing.Request.CompatProfile,
                };
                probeRequestsById[id] = existing with { Request = updated };
            }
            else
            {
                NetworkAccessApprovalLimits limits = ResolveApprovalLimits(current);
                int pendingProbes = probeRequestsById.Values.Count(entry => entry.Request.Status == "pending");
                int userPendingProbes = probeRequestsById.Values.Count(entry => entry.Request.Status == "pending" && string.Equals(entry.Request.UserId, userId, StringComparison.Ordinal));
                if (current.Requests.Count(r => r.Status == "pending") + pendingProbes >= limits.Global
                    || current.Requests.Count(r => r.Status == "pending" && string.Equals(r.UserId, userId, StringComparison.Ordinal)) + userPendingProbes >= limits.PerUser)
                    return null;
                var req = new NetworkAccessRequest(
                    id,
                    userId,
                    agentId,
                    connectorId,
                    attribution,
                    destination,
                    destKind,
                    port,
                    actorKind,
                    now,
                    now,
                    Attempts: 1,
                    Status: "pending",
                    origin,
                    Purpose: "probe",
                    CompatProfile: compatProfile);
                probeRequestsById[id] = new TemporaryProbePendingEntry(containerId, req);
            }

            lastRequestActivityWatermark = Math.Max(lastRequestActivityWatermark + 1, checked(current.Revision + 1));
            return id;
        }
    }

    public void ClearProbeContainer(string containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            return;
        }

        string trimmed = containerId.Trim();
        lock (gate)
        {
            bool changed = probeGrants.RemoveAll(g => string.Equals(g.ContainerId, trimmed, StringComparison.Ordinal)) > 0;
            foreach (string key in probeRequestsById
                .Where(kv => string.Equals(kv.Value.ContainerId, trimmed, StringComparison.Ordinal))
                .Select(static kv => kv.Key)
                .ToArray())
            {
                probeRequestsById.Remove(key);
                changed = true;
            }

            if (changed)
            {
                NetworkAccessPolicyState current = Load();
                lastRequestActivityWatermark = Math.Max(lastRequestActivityWatermark + 1, checked(current.Revision + 1));
            }
        }
    }

    public void PromoteProbeContainerId(string previousContainerId, string actualContainerId)
    {
        if (string.IsNullOrWhiteSpace(previousContainerId) || string.IsNullOrWhiteSpace(actualContainerId))
        {
            return;
        }

        string prev = previousContainerId.Trim();
        string actual = actualContainerId.Trim();
        lock (gate)
        {
            for (int i = 0; i < probeGrants.Count; i++)
            {
                if (string.Equals(probeGrants[i].ContainerId, prev, StringComparison.Ordinal))
                {
                    probeGrants[i] = probeGrants[i] with { ContainerId = actual };
                }
            }

            foreach (string key in probeRequestsById
                .Where(kv => string.Equals(kv.Value.ContainerId, prev, StringComparison.Ordinal))
                .Select(static kv => kv.Key)
                .ToArray())
            {
                probeRequestsById[key] = probeRequestsById[key] with { ContainerId = actual };
            }
        }
    }

    private DecideRequestResult DecideProbeRequestUnsafe(
        NetworkAccessActor actor,
        NetworkAccessPolicyState current,
        TemporaryProbePendingEntry probeEntry,
        bool allow,
        long expectedRevision,
        bool allowLan)
    {
        NetworkAccessRequest request = probeEntry.Request;
        NetworkAccessPolicyAuthorizer.AuthorizeDecideProbeRequest(
            actor,
            request,
            allow,
            allowLan,
            (userId, isTargetAdmin) => ResolveUserPrivilegesUnsafe(ref current, userId, isTargetAdmin));

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

        DateTimeOffset now = DateTimeOffset.UtcNow;
        NetworkAccessRequest updatedRequest = request with
        {
            Status = allow ? "allowed" : "blocked",
            LastSeenUtc = now,
        };
        probeRequestsById[request.Id] = probeEntry with { Request = updatedRequest };

        string? normalizedUser = string.IsNullOrWhiteSpace(request.UserId) ? null : request.UserId.Trim();
        string? normalizedConnector = string.IsNullOrWhiteSpace(request.ConnectorId) ? null : request.ConnectorId.Trim();
        probeGrants.RemoveAll(g =>
            string.Equals(g.ContainerId, probeEntry.ContainerId, StringComparison.Ordinal)
            && string.Equals(g.UserId, normalizedUser, StringComparison.Ordinal)
            && string.Equals(g.ConnectorId, normalizedConnector, StringComparison.Ordinal)
            && string.Equals(g.DestKind, destKind, StringComparison.Ordinal)
            && string.Equals(g.Destination, normalizedDest, StringComparison.Ordinal)
            && g.Port == port);
        probeGrants.Add(new TemporaryProbeGrant(
            probeEntry.ContainerId,
            normalizedUser,
            normalizedConnector,
            destKind,
            normalizedDest,
            port,
            allow,
            allow && allowLan && actor.IsAdmin));

        lastRequestActivityWatermark = Math.Max(lastRequestActivityWatermark + 1, checked(current.Revision + 1));

        // Probe approvals never create or modify persistent rules or state.json (v7-14).
        return new DecideRequestResult(
            current.Revision,
            Rule: null,
            updatedRequest,
            Paused: false,
            OpenInternetBadge: false);
    }
}
