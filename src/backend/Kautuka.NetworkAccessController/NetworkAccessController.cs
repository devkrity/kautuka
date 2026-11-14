namespace Kautuka.NetworkAccessController;

/// <summary>
/// Central decision module (C1) hosted inside Manager. Owns the policy store, workload IP registry,
/// audit ring, image compatibility store, and exact-match decision engine for both Envoy
/// <c>ext_authz</c> (<see cref="EvaluateContainerConnection(ContainerConnectionInput)"/>) and Gateway/ChannelHost
/// (<see cref="EvaluateOutboundCheck"/>).
/// </summary>
public sealed class NetworkAccessController
{
    public const string ClusterDfpPublic = "dfp_public";
    public const string ClusterDfpLan = "dfp_lan";
    public const string ClusterOrigDstPublic = "orig_dst_public";
    public const string ClusterOrigDstLan = "orig_dst_lan";

    private readonly NetworkAccessPolicyStore policyStore;
    private readonly IWorkloadRegistry registry;
    private readonly NetworkAccessAuditRing auditRing;
    private readonly Func<bool>? isContainerAuthorizationGateOpen;

    private long checks;
    private long allowed;
    private long denied;
    private long errors;
    private long requestOverflow;
    private volatile bool envoyListenerHealthy;

    public NetworkAccessController(
        NetworkAccessPolicyStore policyStore,
        IWorkloadRegistry registry,
        NetworkAccessAuditRing? auditRing = null,
        Func<bool>? isContainerAuthorizationGateOpen = null)
    {
        ArgumentNullException.ThrowIfNull(policyStore);
        ArgumentNullException.ThrowIfNull(registry);
        this.policyStore = policyStore;
        this.registry = registry;
        this.auditRing = auditRing ?? new NetworkAccessAuditRing();
        this.isContainerAuthorizationGateOpen = isContainerAuthorizationGateOpen;

        // Fail closed on startup if state.json is malformed (C12).
        this.policyStore.EnsureLoaded();
        this.registry.ContainerDeregistered += (_, args) => this.policyStore.ClearProbeContainer(args.ContainerId);
        this.registry.ContainerIdPromoted += (_, args) => this.policyStore.PromoteProbeContainerId(args.PreviousContainerId, args.ActualContainerId);
    }

    public void ClearProbeContainer(string containerId) => policyStore.ClearProbeContainer(containerId);

    public NetworkAccessPolicyStore PolicyStore => policyStore;

    public IWorkloadRegistry Registry => registry;

    public NetworkAccessAuditRing AuditRing => auditRing;

    public long Checks => Interlocked.Read(ref checks);

    public long AllowedCount => Interlocked.Read(ref allowed);

    public long DeniedCount => Interlocked.Read(ref denied);

    public long Errors => Interlocked.Read(ref errors);

    public long RequestOverflow => Interlocked.Read(ref requestOverflow);

    public bool EnvoyListenerHealthy
    {
        get => envoyListenerHealthy;
        set => envoyListenerHealthy = value;
    }

    public void RecordError() => Interlocked.Increment(ref errors);

    public NetworkAccessStatusSnapshot GetStatus()
        => new(
            policyStore.Revision,
            registry.Count,
            Checks,
            AllowedCount,
            DeniedCount,
            Errors,
            RequestOverflow,
            registry.RegistryConflicts,
            envoyListenerHealthy);

    public NetworkAccessDecisionResult EvaluateContainerConnection(ContainerConnectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return EvaluateContainerConnection(
            input.SourceIp,
            input.DestinationIp,
            input.DestinationPort,
            input.IsTls ?? (input.Sni is not null),
            input.Sni);
    }

    /// <summary>
    /// Evaluates an outbound container connection from Envoy's <c>ext_authz</c> check
    /// following the exact Section 4.1.3 order.
    /// </summary>
    public NetworkAccessDecisionResult EvaluateContainerConnection(
        string? sourceIp,
        string? destinationIp,
        int destinationPort,
        bool isTls,
        string? sni)
    {
        Interlocked.Increment(ref checks);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // The topology gate revokes new container authorization decisions during boundary
        // incidents. It does not revoke established connections; workload containment handles those.
        if (isContainerAuthorizationGateOpen is not null)
        {
            bool gateOpen;
            try
            {
                gateOpen = isContainerAuthorizationGateOpen();
            }
            catch
            {
                gateOpen = false;
            }

            if (!gateOpen)
            {
                bool foundEntry = registry.TryGetByIp(sourceIp, out RegistryEntry? gateEntry);
                return RecordDeny(
                    now,
                    sourceIp,
                    foundEntry ? gateEntry : null,
                    sni ?? destinationIp ?? string.Empty,
                    destinationPort,
                    "topology_closed");
            }
        }

        // 0. Emergency Brake check FIRST (Section 4.1.4 / C21)
        NetworkAccessPolicyState state = policyStore.GetRawStateSnapshot();
        bool foundSource = registry.TryGetByIp(sourceIp, out RegistryEntry? entry);

        if (state.Brake.System)
        {
            return RecordDeny(now, sourceIp, entry, sni ?? destinationIp ?? string.Empty, destinationPort, "brake_system");
        }

        if (foundSource && entry is not null && !string.IsNullOrEmpty(entry.UserId) && state.Brake.IsUserBraked(entry.UserId))
        {
            return RecordDeny(now, sourceIp, entry, sni ?? destinationIp ?? string.Empty, destinationPort, "brake_user");
        }

        // 1. Source IP not in registry -> deny (unknown_source, no request recorded)
        if (!foundSource || entry is null)
        {
            return RecordDeny(now, sourceIp, null, sni ?? destinationIp ?? string.Empty, destinationPort, "unknown_source");
        }

        // 1a. Entry has egress == deny-all or proxy-only -> deny (deny_all, no request recorded)
        if (entry.Egress is "deny-all" or "proxy-only")
        {
            return RecordDeny(now, sourceIp, entry, sni ?? destinationIp ?? string.Empty, destinationPort, "deny_all");
        }

        if (destinationPort is < 1 or > 65535)
        {
            return RecordDeny(now, sourceIp, entry, sni ?? destinationIp ?? string.Empty, destinationPort, "bad_destination");
        }

        string destKind;
        string normalizedDestination;

        if (isTls)
        {
            // 3. TLS with no SNI -> deny (tls_no_sni, no request recorded)
            if (string.IsNullOrEmpty(sni))
            {
                return RecordDeny(now, sourceIp, entry, destinationIp ?? string.Empty, destinationPort, "tls_no_sni");
            }

            // 2. TLS with SNI -> normalise; if malformed -> deny (bad_sni, no request recorded)
            if (!NetworkDestinationNormalizer.TryNormalizeHostname(sni, out normalizedDestination, out _))
            {
                return RecordDeny(now, sourceIp, entry, sni, destinationPort, "bad_sni");
            }

            destKind = "host";
        }
        else
        {
            // 4. Plain TCP -> validate IP; if sinkhole 198.18.250.250 -> deny (sinkhole_plain, no request recorded)
            if (!NetworkDestinationNormalizer.TryNormalizeIpv4(destinationIp, out normalizedDestination, out _))
            {
                return RecordDeny(now, sourceIp, entry, destinationIp ?? string.Empty, destinationPort, "bad_destination");
            }

            if (string.Equals(normalizedDestination, NetworkAddressClassifier.SinkholeAddress, StringComparison.Ordinal))
            {
                return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "sinkhole_plain");
            }

            destKind = "ip";
        }

        bool isProbe = string.Equals(entry.Purpose, "probe", StringComparison.OrdinalIgnoreCase);

        // 5. Ephemeral in-memory probe grant/block check (v7-14) BEFORE persistent rule evaluation.
        if (isProbe && policyStore.TryEvaluateTemporaryProbeDecision(
                entry.ContainerId,
                entry.UserId,
                entry.ConnectorId,
                destKind,
                normalizedDestination,
                destinationPort,
                out bool tempAllowed,
                out bool tempAllowLan))
        {
            if (tempAllowed)
            {
                string cluster = GetClusterName(isTls, tempAllowLan);
                return RecordAllow(now, sourceIp, entry, normalizedDestination, destinationPort, "probe_temporary_allow", cluster, openInternetBadge: false);
            }

            return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "blocked");
        }

        // 6. Scope resolution & exact-match rule lookup
        ScopeMatchOutcome match = ResolveContainerScopeRule(state, entry, destKind, normalizedDestination, destinationPort);

        switch (match.Kind)
        {
            case ScopeMatchKind.Allowed:
                string cluster = GetClusterName(isTls, match.AllowLan);
                return RecordAllow(now, sourceIp, entry, normalizedDestination, destinationPort, match.Reason, cluster, match.OpenInternetBadge);

            case ScopeMatchKind.DeniedNotPublic:
                return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "not_public");

            case ScopeMatchKind.BlockedByRule:
                policyStore.IncrementBlockedRequestAttemptInMemory(
                    entry.UserId,
                    entry.AgentId,
                    entry.ConnectorId,
                    entry.Systemwide,
                    destKind,
                    normalizedDestination,
                    destinationPort);
                return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "blocked");

            default:
                // 7. No rule at any applicable scope -> deny (no_rule) and record/dedupe pending request
                string actorKind = entry.Kind == "connector" ? "connector" : "agent";
                if (isProbe)
                {
                    string? probeRequestId = policyStore.RecordOrUpdatePendingProbeRequest(
                        entry.ContainerId,
                        entry.UserId,
                        entry.AgentId,
                        entry.ConnectorId,
                        entry.Systemwide,
                        entry.Attribution,
                        destKind,
                        normalizedDestination,
                        destinationPort,
                        actorKind,
                        origin: "container",
                        compatProfile: entry.CompatProfile,
                        observedAtUtc: now);
                    if (probeRequestId is null)
                    {
                        Interlocked.Increment(ref requestOverflow);
                        return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "request_overflow");
                    }

                    return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "no_rule", probeRequestId);
                }

                (string? requestId, bool overflow) = policyStore.RecordOrUpdatePendingRequest(
                    entry.UserId,
                    entry.AgentId,
                    entry.ConnectorId,
                    entry.Systemwide,
                    entry.Attribution,
                    destKind,
                    normalizedDestination,
                    destinationPort,
                    actorKind,
                    origin: "container",
                    purpose: null,
                    compatProfile: entry.CompatProfile,
                    observedAtUtc: now);

                if (overflow)
                {
                    Interlocked.Increment(ref requestOverflow);
                    return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "request_overflow");
                }

                return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "no_rule", requestId);
        }
    }

    /// <summary>
    /// Evaluates an app-level outbound check (<c>CheckOutbound</c> RPC) from Gateway or ChannelHost.
    /// </summary>
    public NetworkAccessDecisionResult EvaluateOutboundCheck(OutboundCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Interlocked.Increment(ref checks);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(input.UserId))
        {
            return RecordOutboundDeny(now, string.Empty, input.AgentId, input.ConnectorId, input.Host, input.Port, "unknown_source");
        }

        string userId = input.UserId.Trim();
        string agentId = string.IsNullOrWhiteSpace(input.AgentId) ? "gateway" : input.AgentId.Trim();
        string? connectorId = string.IsNullOrWhiteSpace(input.ConnectorId) ? null : input.ConnectorId.Trim();
        string origin = string.IsNullOrWhiteSpace(input.Origin) ? "gateway" : input.Origin.Trim();

        // 0. Emergency Brake check FIRST
        NetworkAccessPolicyState state = policyStore.GetRawStateSnapshot();
        if (state.Brake.System)
        {
            return RecordOutboundDeny(now, userId, agentId, connectorId, input.Host, input.Port, "brake_system");
        }

        if (userId != "system" && state.Brake.IsUserBraked(userId))
        {
            return RecordOutboundDeny(now, userId, agentId, connectorId, input.Host, input.Port, "brake_user");
        }

        if (input.Port is < 1 or > 65535)
        {
            return RecordOutboundDeny(now, userId, agentId, connectorId, input.Host, input.Port, "bad_destination");
        }

        string destKind;
        string normalizedDestination;
        if (NetworkDestinationNormalizer.TryNormalizeIpv4(input.Host, out string normIp, out _))
        {
            if (string.Equals(normIp, NetworkAddressClassifier.SinkholeAddress, StringComparison.Ordinal))
            {
                return RecordOutboundDeny(now, userId, agentId, connectorId, normIp, input.Port, "sinkhole_plain");
            }

            destKind = "ip";
            normalizedDestination = normIp;
        }
        else if (NetworkDestinationNormalizer.TryNormalizeHostname(input.Host, out string normHost, out _))
        {
            destKind = "host";
            normalizedDestination = normHost;
        }
        else
        {
            return RecordOutboundDeny(now, userId, agentId, connectorId, input.Host, input.Port, "bad_sni");
        }

        // System actor check (Gateway / ChannelHost system traffic, seeded providers & channels)
        if (string.Equals(userId, "system", StringComparison.Ordinal))
        {
            NetworkAccessRule? explicitSystemRule = FindSystemExplicitRule(state.Rules, agentId, destKind, normalizedDestination, input.Port);
            if (explicitSystemRule is not null)
            {
                if (explicitSystemRule.Allowed)
                {
                    return RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, "rule_system");
                }

                policyStore.IncrementBlockedRequestAttemptInMemory("system", agentId, null, false, destKind, normalizedDestination, input.Port);
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "blocked");
            }

            NetworkAccessRule? seedRule = FindSystemSeedRule(policyStore.SeedRules, agentId, destKind, normalizedDestination, input.Port);
            if (seedRule is not null && seedRule.Allowed)
            {
                return RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, "seed");
            }

            (string? sysReqId, bool sysOverflow) = policyStore.RecordOrUpdatePendingRequest(
                "system",
                agentId,
                connectorId: null,
                systemwide: false,
                attribution: "none",
                destKind,
                normalizedDestination,
                input.Port,
                actorKind: "first-party",
                origin,
                input.Purpose,
                compatProfile: null,
                observedAtUtc: now);

            if (sysOverflow)
            {
                Interlocked.Increment(ref requestOverflow);
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "request_overflow");
            }

            return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "no_rule", sysReqId);
        }

        // Synthetic RegistryEntry representing the app-level caller for scope resolution
        var syntheticEntry = new RegistryEntry(
            Ip: "127.0.0.1",
            UserId: userId,
            AgentId: input.Systemwide ? null : agentId,
            ContainerId: origin,
            Kind: input.Systemwide || !string.IsNullOrEmpty(connectorId) ? "connector" : "agent-sandbox",
            ConnectorId: connectorId,
            ExecContext: "app",
            Systemwide: input.Systemwide,
            Egress: "normal",
            Attribution: input.Systemwide ? "user" : "user-agent",
            CompatProfile: null,
            RegisteredUtc: now);

        ScopeMatchOutcome match = ResolveContainerScopeRule(state, syntheticEntry, destKind, normalizedDestination, input.Port);
        switch (match.Kind)
        {
            case ScopeMatchKind.Allowed:
                return RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, match.Reason, match.OpenInternetBadge);

            case ScopeMatchKind.DeniedNotPublic:
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "not_public");

            case ScopeMatchKind.BlockedByRule:
                policyStore.IncrementBlockedRequestAttemptInMemory(
                    userId,
                    input.Systemwide ? null : agentId,
                    connectorId,
                    input.Systemwide,
                    destKind,
                    normalizedDestination,
                    input.Port);
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "blocked");

            default:
                // Admin-granted systemwide first-party endpoint rules (e.g. AI providers approved at test-connection time)
                NetworkAccessRule? systemwideRule = FindExactRule(
                    state.Rules,
                    scopeKind: "system",
                    userId: null,
                    agentId: null,
                    connectorId: null,
                    destKind,
                    normalizedDestination,
                    input.Port);
                if (systemwideRule is not null)
                {
                    if (!systemwideRule.Allowed)
                    {
                        return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "blocked");
                    }

                    if (destKind == "ip"
                        && NetworkAddressClassifier.IsNonPublicV4(normalizedDestination)
                        && !systemwideRule.AllowLan)
                    {
                        return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "not_public");
                    }

                    return RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, "rule_system");
                }

                (string? reqId, bool overflow) = policyStore.RecordOrUpdatePendingRequest(
                    userId,
                    input.Systemwide ? null : agentId,
                    connectorId,
                    input.Systemwide,
                    attribution: input.Systemwide ? "user" : "user-agent",
                    destKind,
                    normalizedDestination,
                    input.Port,
                    actorKind: "first-party",
                    origin,
                    input.Purpose,
                    compatProfile: null,
                    observedAtUtc: now);

                if (overflow)
                {
                    Interlocked.Increment(ref requestOverflow);
                    return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "request_overflow");
                }

                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "no_rule", reqId);
        }
    }

    private ScopeMatchOutcome ResolveContainerScopeRule(
        NetworkAccessPolicyState state,
        RegistryEntry entry,
        string destKind,
        string destination,
        int port)
    {
        if (!entry.Systemwide && entry.Attribution == "user-agent")
        {
            // (a) Exact agent (userId, agentId) rule first
            NetworkAccessRule? agentRule = FindExactRule(
                state.Rules,
                scopeKind: "agent",
                userId: entry.UserId,
                agentId: entry.AgentId,
                connectorId: null,
                destKind,
                destination,
                port);
            if (agentRule is not null)
            {
                return agentRule.Allowed
                    ? new ScopeMatchOutcome(ScopeMatchKind.Allowed, "rule_agent", agentRule.AllowLan, OpenInternetBadge: false)
                    : new ScopeMatchOutcome(ScopeMatchKind.BlockedByRule, "blocked", false, false);
            }

            // (b) Exact user (userId, connectorId == null) rule — active ONLY while user holds both flags!
            if (!string.IsNullOrEmpty(entry.UserId) && policyStore.HasBothPrivileges(entry.UserId))
            {
                NetworkAccessRule? userRule = FindExactRule(
                    state.Rules,
                    scopeKind: "user",
                    userId: entry.UserId,
                    agentId: null,
                    connectorId: null,
                    destKind,
                    destination,
                    port);
                if (userRule is not null)
                {
                    return userRule.Allowed
                        ? new ScopeMatchOutcome(ScopeMatchKind.Allowed, "rule_user", userRule.AllowLan, OpenInternetBadge: false)
                        : new ScopeMatchOutcome(ScopeMatchKind.BlockedByRule, "blocked", false, false);
                }
            }

            // (c) Dedicated connector container: check admin any-public rule for (userId, connectorId)
            if (!string.IsNullOrEmpty(entry.UserId)
                && !string.IsNullOrEmpty(entry.ConnectorId)
                && entry.ExecContext is "isolated" or "isolated-with-agent-files")
            {
                NetworkAccessRule? anyPublicRule = FindAnyPublicRule(state.Rules, entry.UserId, entry.ConnectorId);
                if (anyPublicRule is { Allowed: true })
                {
                    if (destKind == "ip" && NetworkAddressClassifier.IsNonPublicV4(destination))
                    {
                        return new ScopeMatchOutcome(ScopeMatchKind.DeniedNotPublic, "not_public", false, false);
                    }

                    return new ScopeMatchOutcome(ScopeMatchKind.Allowed, "any_public", AllowLan: false, OpenInternetBadge: true);
                }
            }

            return new ScopeMatchOutcome(ScopeMatchKind.NoMatch, "no_rule", false, false);
        }

        if (entry.Attribution == "user")
        {
            // Systemwide connector per-user instance OR user-attributed connector probe
            // (a) user (userId, connectorId) exact rule first
            NetworkAccessRule? userConnectorRule = FindExactRule(
                state.Rules,
                scopeKind: "user",
                userId: entry.UserId,
                agentId: null,
                connectorId: entry.ConnectorId,
                destKind,
                destination,
                port);
            if (userConnectorRule is not null)
            {
                return userConnectorRule.Allowed
                    ? new ScopeMatchOutcome(ScopeMatchKind.Allowed, "rule_user_connector", userConnectorRule.AllowLan, OpenInternetBadge: false)
                    : new ScopeMatchOutcome(ScopeMatchKind.BlockedByRule, "blocked", false, false);
            }

            // (a2) user (userId, connectorId) any-public rule
            if (!string.IsNullOrEmpty(entry.UserId) && !string.IsNullOrEmpty(entry.ConnectorId))
            {
                NetworkAccessRule? anyPublicRule = FindAnyPublicRule(state.Rules, entry.UserId, entry.ConnectorId);
                if (anyPublicRule is { Allowed: true })
                {
                    if (destKind == "ip" && NetworkAddressClassifier.IsNonPublicV4(destination))
                    {
                        return new ScopeMatchOutcome(ScopeMatchKind.DeniedNotPublic, "not_public", false, false);
                    }

                    return new ScopeMatchOutcome(ScopeMatchKind.Allowed, "any_public", AllowLan: false, OpenInternetBadge: true);
                }
            }

            // (b) connector-system (connectorId) rule
            NetworkAccessRule? systemConnectorRule = FindExactRule(
                state.Rules,
                scopeKind: "connector-system",
                userId: null,
                agentId: null,
                connectorId: entry.ConnectorId,
                destKind,
                destination,
                port);
            if (systemConnectorRule is not null)
            {
                return systemConnectorRule.Allowed
                    ? new ScopeMatchOutcome(ScopeMatchKind.Allowed, "rule_connector_system", systemConnectorRule.AllowLan, OpenInternetBadge: false)
                    : new ScopeMatchOutcome(ScopeMatchKind.BlockedByRule, "blocked", false, false);
            }

            return new ScopeMatchOutcome(ScopeMatchKind.NoMatch, "no_rule", false, false);
        }

        if (entry.Systemwide && entry.Attribution == "none")
        {
            // Shared systemwide connector with no user attribution: only connector-system applies
            NetworkAccessRule? systemConnectorRule = FindExactRule(
                state.Rules,
                scopeKind: "connector-system",
                userId: null,
                agentId: null,
                connectorId: entry.ConnectorId,
                destKind,
                destination,
                port);
            if (systemConnectorRule is not null)
            {
                return systemConnectorRule.Allowed
                    ? new ScopeMatchOutcome(ScopeMatchKind.Allowed, "rule_connector_system", systemConnectorRule.AllowLan, OpenInternetBadge: false)
                    : new ScopeMatchOutcome(ScopeMatchKind.BlockedByRule, "blocked", false, false);
            }
        }

        return new ScopeMatchOutcome(ScopeMatchKind.NoMatch, "no_rule", false, false);
    }

    private static NetworkAccessRule? FindExactRule(
        IReadOnlyList<NetworkAccessRule> rules,
        string scopeKind,
        string? userId,
        string? agentId,
        string? connectorId,
        string destKind,
        string destination,
        int port)
    {
        foreach (NetworkAccessRule rule in rules)
        {
            if (string.Equals(rule.ScopeKind, scopeKind, StringComparison.Ordinal)
                && string.Equals(rule.UserId ?? string.Empty, userId ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(rule.AgentId ?? string.Empty, agentId ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(rule.ConnectorId ?? string.Empty, connectorId ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(rule.DestKind, destKind, StringComparison.Ordinal)
                && string.Equals(rule.Destination, destination, StringComparison.Ordinal)
                && rule.Port == port)
            {
                return rule;
            }
        }

        return null;
    }

    private static NetworkAccessRule? FindAnyPublicRule(
        IReadOnlyList<NetworkAccessRule> rules,
        string userId,
        string connectorId)
    {
        foreach (NetworkAccessRule rule in rules)
        {
            if (string.Equals(rule.ScopeKind, "user", StringComparison.Ordinal)
                && string.Equals(rule.UserId, userId, StringComparison.Ordinal)
                && string.IsNullOrEmpty(rule.AgentId)
                && string.Equals(rule.ConnectorId, connectorId, StringComparison.Ordinal)
                && string.Equals(rule.DestKind, "any-public", StringComparison.Ordinal))
            {
                return rule;
            }
        }

        return null;
    }

    private static NetworkAccessRule? FindSystemExplicitRule(
        IReadOnlyList<NetworkAccessRule> rules,
        string agentId,
        string destKind,
        string destination,
        int port)
    {
        NetworkAccessRule? exact = FindExactRule(rules, "agent", "system", agentId, null, destKind, destination, port);
        if (exact is not null)
        {
            return exact;
        }

        if (!string.Equals(agentId, "system", StringComparison.Ordinal))
        {
            NetworkAccessRule? wildcardSystem = FindExactRule(rules, "agent", "system", "system", null, destKind, destination, port);
            if (wildcardSystem is not null)
            {
                return wildcardSystem;
            }
        }

        return null;
    }

    private static NetworkAccessRule? FindSystemSeedRule(
        IReadOnlyList<NetworkAccessRule> seeds,
        string agentId,
        string destKind,
        string destination,
        int port)
    {
        foreach (NetworkAccessRule seed in seeds)
        {
            if (string.Equals(seed.UserId, "system", StringComparison.Ordinal)
                && (string.Equals(seed.AgentId, agentId, StringComparison.Ordinal) || string.Equals(agentId, "system", StringComparison.Ordinal))
                && string.Equals(seed.DestKind, destKind, StringComparison.Ordinal)
                && string.Equals(seed.Destination, destination, StringComparison.Ordinal)
                && seed.Port == port)
            {
                return seed;
            }
        }

        return null;
    }

    private static string GetClusterName(bool isTls, bool allowLan)
        => isTls
            ? (allowLan ? ClusterDfpLan : ClusterDfpPublic)
            : (allowLan ? ClusterOrigDstLan : ClusterOrigDstPublic);

    private NetworkAccessDecisionResult RecordAllow(
        DateTimeOffset now,
        string? sourceIp,
        RegistryEntry entry,
        string destination,
        int port,
        string reason,
        string cluster,
        bool openInternetBadge)
    {
        Interlocked.Increment(ref allowed);
        auditRing.Record(new NetworkAccessActivityEntry(
            now,
            sourceIp,
            entry.UserId,
            entry.AgentId,
            entry.ConnectorId,
            destination,
            port,
            Decision: "allow",
            reason,
            cluster,
            openInternetBadge,
            CompatibilityMode: !string.IsNullOrEmpty(entry.CompatProfile)));
        return new NetworkAccessDecisionResult(true, reason, cluster, RequestId: null, openInternetBadge);
    }

    private NetworkAccessDecisionResult RecordDeny(
        DateTimeOffset now,
        string? sourceIp,
        RegistryEntry? entry,
        string destination,
        int port,
        string reason,
        string? requestId = null)
    {
        Interlocked.Increment(ref denied);
        auditRing.Record(new NetworkAccessActivityEntry(
            now,
            sourceIp,
            entry?.UserId,
            entry?.AgentId,
            entry?.ConnectorId,
            destination,
            port,
            Decision: "deny",
            reason,
            Cluster: null,
            OpenInternetBadge: false,
            CompatibilityMode: !string.IsNullOrEmpty(entry?.CompatProfile)));
        return new NetworkAccessDecisionResult(false, reason, Cluster: null, requestId, OpenInternetBadge: false);
    }

    private NetworkAccessDecisionResult RecordOutboundAllow(
        DateTimeOffset now,
        string userId,
        string agentId,
        string? connectorId,
        string destination,
        int port,
        string reason,
        bool openInternetBadge = false)
    {
        Interlocked.Increment(ref allowed);
        auditRing.Record(new NetworkAccessActivityEntry(
            now,
            SourceIp: null,
            userId,
            agentId,
            connectorId,
            destination,
            port,
            Decision: "allow",
            reason,
            Cluster: null,
            openInternetBadge,
            CompatibilityMode: false));
        return new NetworkAccessDecisionResult(true, reason, Cluster: null, RequestId: null, openInternetBadge);
    }

    private NetworkAccessDecisionResult RecordOutboundDeny(
        DateTimeOffset now,
        string userId,
        string agentId,
        string? connectorId,
        string destination,
        int port,
        string reason,
        string? requestId = null)
    {
        Interlocked.Increment(ref denied);
        auditRing.Record(new NetworkAccessActivityEntry(
            now,
            SourceIp: null,
            userId,
            agentId,
            connectorId,
            destination,
            port,
            Decision: "deny",
            reason,
            Cluster: null,
            OpenInternetBadge: false,
            CompatibilityMode: false));
        return new NetworkAccessDecisionResult(false, reason, Cluster: null, requestId, OpenInternetBadge: false);
    }

    private enum ScopeMatchKind
    {
        NoMatch,
        Allowed,
        BlockedByRule,
        DeniedNotPublic,
    }

    private readonly record struct ScopeMatchOutcome(
        ScopeMatchKind Kind,
        string Reason,
        bool AllowLan,
        bool OpenInternetBadge);
}
