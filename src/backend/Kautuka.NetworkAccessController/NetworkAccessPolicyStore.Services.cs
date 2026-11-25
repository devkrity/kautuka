namespace Kautuka.NetworkAccessController;

public sealed record ServicePermissionView(string ServiceId, int Total, int Granted, int Effective);

public sealed partial class NetworkAccessPolicyStore
{
    public (long Revision, IReadOnlyList<NetworkAccessRuleView> Rules, IReadOnlyList<ServicePermissionView> Services) ListRulesWithServices(
        NetworkAccessActor actor, string? filterUserId)
    {
        lock (gate)
        {
            (long revision, IReadOnlyList<NetworkAccessRuleView> rules) = ListRules(actor, filterUserId);
            if (!actor.IsAdmin) return (revision, rules, []);
            NetworkAccessPolicyState current = Load();
            ServicePermissionView[] services = [.. seedRules.Where(IsServiceTarget)
                .GroupBy(rule => rule.SeedSetupService!, StringComparer.Ordinal)
                .Select(group =>
                {
                    NetworkAccessRule[] targets = [.. group];
                    int granted = targets.Count(target => current.Rules.FirstOrDefault(rule => rule.Id == target.Id)?.Allowed ?? target.Allowed);
                    int effective = targets.Count(target => (current.Rules.FirstOrDefault(rule => rule.Id == target.Id)?.Allowed ?? target.Allowed)
                        && !ServiceTargetRestricted(current, target));
                    return new ServicePermissionView(group.Key, targets.Length, granted, effective);
                })];
            return (revision, rules, services);
        }
    }

    private static bool IsServiceTarget(NetworkAccessRule rule) => !string.IsNullOrWhiteSpace(rule.SeedSetupService)
        && rule.ScopeKind == "agent" && rule.UserId == "system" && rule.AgentId is not null && rule.DestKind == "host";

    private bool ServiceTargetRestricted(NetworkAccessPolicyState current, NetworkAccessRule target) => current.Brake.System ||
        current.Brake.IsUserBraked("system") ||
        NetworkAccessRuleMatcher.FindApplicable(RegistrableDomain, current, target.UserId, target.AgentId, null,
            target.DestKind, target.Destination, target.Port)
            .Any(static rule => !rule.Allowed);

    public (long Revision, string Status) SetServicePermission(NetworkAccessActor actor, string serviceId, bool allowed, long expectedRevision)
    {
        NetworkAccessPolicyAuthorizer.ValidateActor(actor);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("Only an administrator can change service permissions.");
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        if (serviceId == "model-providers") throw new ArgumentException("Model providers are managed individually.", nameof(serviceId));
        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            if (current.Revision != expectedRevision) throw new InvalidOperationException("revision_conflict: Policy revision has changed.");
            NetworkAccessRule[] targets = seedRules.Where(rule => IsServiceTarget(rule) && rule.SeedSetupService == serviceId).ToArray();
            if (targets.Length == 0) throw new ArgumentException("Unknown service.", nameof(serviceId));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var ids = targets.Select(static rule => rule.Id).ToHashSet(StringComparer.Ordinal);
            NetworkAccessRule[] rules = [.. current.Rules.Where(rule => !ids.Contains(rule.Id)),
                .. targets.Select(rule => rule with { Allowed = allowed, AllowLan = false, DecidedBy = actor.UserId,
                    DecidedAtUtc = now, Source = "user", DecidedByAdmin = true })];
            if (rules.Length > MaxRules) throw new InvalidOperationException("rule_limit_reached: Maximum rule count exceeded.");
            NetworkAccessPolicyState updated = current with { Rules = rules };
            NetworkAccessRequest[] requests = [.. current.Requests.Select(req => req.Status == "pending" && ShouldResolveMatchingPendingRequest(updated, req)
                ? req with { Status = "allowed", LastSeenUtc = now } : req)];
            long revision = checked(current.Revision + 1);
            updated = updated with { Revision = revision, Requests = requests };
            bool restricted = allowed && targets.Any(target => ServiceTargetRestricted(updated, target));
            SaveSync(updated);
            return (revision, !allowed ? "paused" : restricted ? "restricted" : "allowed");
        }
    }
}
