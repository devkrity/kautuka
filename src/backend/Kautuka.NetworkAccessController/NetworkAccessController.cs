namespace Kautuka.NetworkAccessController;

/// <summary>
/// Central decision module (C1) hosted inside Manager. Owns the policy store, workload IP registry,
/// audit ring, image compatibility store, and exact-match decision engine for both Envoy
/// <c>ext_authz</c> (<see cref="EvaluateContainerConnection(ContainerConnectionInput)"/>) and Gateway/ChannelHost
/// (<see cref="EvaluateOutboundCheck"/>).
/// </summary>
public sealed class NetworkAccessController
{
    private sealed record ProviderTestGrant(string UserId, string Host, int Port, bool AllowLan, DateTimeOffset ExpiresAt);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderTestGrant> providerTests = new(StringComparer.Ordinal);

    public string BeginProviderTest(NetworkAccessActor actor, string host, int port, bool allowLan)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("Provider tests require an administrator.");
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        string normalized;
        if (!NetworkDestinationNormalizer.TryNormalizeIpv4(host, out normalized, out _)
            && !NetworkDestinationNormalizer.TryNormalizeHostname(host, out normalized, out _))
            throw new ArgumentException("Invalid provider host.", nameof(host));
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (var entry in providerTests)
        {
            if (entry.Value.ExpiresAt <= now)
                providerTests.TryRemove(entry.Key, out _);
        }
        string token = Guid.NewGuid().ToString("N");
        providerTests[token] = new(actor.UserId, normalized, port, allowLan, now.AddSeconds(30));
        return token;
    }

    public void EndProviderTest(NetworkAccessActor actor, string token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("Provider tests require an administrator.");
        if (providerTests.TryGetValue(token, out ProviderTestGrant? grant) && grant.UserId == actor.UserId)
            providerTests.TryRemove(token, out _);
    }
    public const string ClusterDfpPublic = "dfp_public";
    public const string ClusterDfpLan = "dfp_lan";
    public const string ClusterOrigDstPublic = "orig_dst_public";
    public const string ClusterOrigDstLan = "orig_dst_lan";
    public const string ClusterPackageProxy = "pkgs_proxy";

    private readonly NetworkAccessPolicyStore policyStore;
    private readonly IWorkloadRegistry registry;
    private readonly NetworkAccessAuditRing auditRing;
    private readonly Func<bool>? isContainerAuthorizationGateOpen;
    private readonly Func<bool>? isPackageProxyReady;
    private readonly TimeProvider timeProvider;

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
        Func<bool>? isContainerAuthorizationGateOpen = null,
        Func<bool>? isPackageProxyReady = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(policyStore);
        ArgumentNullException.ThrowIfNull(registry);
        this.policyStore = policyStore;
        this.registry = registry;
        this.auditRing = auditRing ?? new NetworkAccessAuditRing();
        this.isContainerAuthorizationGateOpen = isContainerAuthorizationGateOpen;
        this.isPackageProxyReady = isPackageProxyReady;
        this.timeProvider = timeProvider ?? TimeProvider.System;

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

        // 1a. deny-all remains absolute. proxy-only is constrained below to the one
        // Manager-seeded package-cache service destination.
        if (entry.Egress == "deny-all")
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

        bool isPackageProxyService = IsPackageProxyService(destKind, normalizedDestination, destinationPort);
        if (isPackageProxyService && HasPackageServiceExplicitDeny(state, entry, destKind, normalizedDestination, destinationPort))
        {
            policyStore.IncrementBlockedRequestAttemptInMemory(
                entry.UserId, entry.AgentId, entry.ConnectorId, destKind, normalizedDestination, destinationPort);
            return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "blocked");
        }

        if (isPackageProxyService && isPackageProxyReady?.Invoke() == false)
        {
            return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "package_proxy_unavailable");
        }
        if (entry.Egress == "proxy-only" && !isPackageProxyService)
        {
            return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "deny_all");
        }

        bool isProbe = string.Equals(entry.Purpose, "probe", StringComparison.OrdinalIgnoreCase);

        // An explicit block always wins, including over a temporary probe allowance.
        ScopeMatchOutcome match = ResolveContainerScopeRule(state, entry, destKind, normalizedDestination, destinationPort);
        if (match.Kind == ScopeMatchKind.BlockedByRule)
            return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "blocked");

        if (entry.Ip == NetworkAddressClassifier.ApplianceChannelHostAddress)
        {
            if (entry.Purpose == "appliance-channelhost" && entry.UserId == "system" && entry.AgentId == "channelhost"
                && isTls && destinationIp == NetworkAddressClassifier.SinkholeAddress)
            {
                ScopeMatchOutcome channel = ResolveChannelProviderPermission(state, destKind, normalizedDestination, destinationPort);
                return channel.Kind == ScopeMatchKind.Allowed
                    ? RecordAllow(now, sourceIp, entry, normalizedDestination, destinationPort, channel.Reason, ClusterDfpPublic, openInternetBadge: false)
                    : RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, channel.Reason);
            }

            return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "channel_provider_denied");
        }

        if (entry.Ip == NetworkAddressClassifier.ApplianceUpdateServiceAddress)
        {
            if (entry.Purpose == "appliance-updateservice" && entry.UserId == "system" && entry.AgentId == "updateservice"
                && isTls && destinationPort == 443 && destinationIp == NetworkAddressClassifier.SinkholeAddress
                && normalizedDestination is ("api.github.com" or "github.com" or "release-assets.githubusercontent.com")
                && FindExactRule(state.Rules, "agent", "system", "updateservice", null, destKind, normalizedDestination, destinationPort)
                    is { Allowed: true } releaseRule && !NetworkAccessRuleMatcher.IsPaused(state, releaseRule))
                return RecordAllow(now, sourceIp, entry, normalizedDestination, destinationPort,
                    "release_provider", ClusterDfpPublic, openInternetBadge: false);

            return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "release_provider_denied");
        }

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
                string cluster = GetDecisionClusterName(isTls, tempAllowLan, destKind, normalizedDestination, destinationPort);
                return RecordAllow(now, sourceIp, entry, normalizedDestination, destinationPort, "probe_temporary_allow", cluster, openInternetBadge: false);
            }

            return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "blocked");
        }

        // 6. Scope resolution & exact-match rule lookup
        switch (match.Kind)
        {
            case ScopeMatchKind.Allowed:
                string cluster = GetDecisionClusterName(isTls, match.AllowLan, destKind, normalizedDestination, destinationPort);
                return RecordAllow(now, sourceIp, entry, normalizedDestination, destinationPort, match.Reason, cluster, match.OpenInternetBadge);

            case ScopeMatchKind.DeniedNotPublic:
                return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "not_public");

            case ScopeMatchKind.BlockedByRule:
                policyStore.IncrementBlockedRequestAttemptInMemory(
                    entry.UserId,
                    entry.AgentId,
                    entry.ConnectorId,
                    destKind,
                    normalizedDestination,
                    destinationPort);
                return RecordDeny(now, sourceIp, entry, normalizedDestination, destinationPort, "blocked");

            default:
                if (isPackageProxyService)
                {
                    ScopeMatchOutcome serviceDefault = ResolvePackageServiceDefault(state, destKind, normalizedDestination, destinationPort);
                    if (serviceDefault.Kind == ScopeMatchKind.Allowed)
                    {
                        string serviceCluster = GetDecisionClusterName(isTls, serviceDefault.AllowLan, destKind, normalizedDestination, destinationPort);
                        return RecordAllow(now, sourceIp, entry, normalizedDestination, destinationPort,
                            serviceDefault.Reason, serviceCluster, serviceDefault.OpenInternetBadge);
                    }
                }

                // 7. No rule at any applicable scope -> deny (no_rule) and record/dedupe pending request
                string actorKind = entry.Kind == "connector" ? "connector" : "agent";
                if (isProbe)
                {
                    string? probeRequestId = policyStore.RecordOrUpdatePendingProbeRequest(
                        entry.ContainerId,
                        entry.UserId,
                        entry.AgentId,
                        entry.ConnectorId,
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
        bool userConnectorWithoutAgent = connectorId is not null && string.IsNullOrWhiteSpace(input.AgentId);
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
            if (agentId == "channelhost")
            {
                ScopeMatchOutcome channel = ResolveChannelProviderPermission(state, destKind, normalizedDestination, input.Port);
                return channel.Kind == ScopeMatchKind.Allowed
                    ? RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, channel.Reason)
                    : RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, channel.Reason);
            }

            IReadOnlyList<NetworkAccessRule> platformRules = NetworkAccessRuleMatcher.FindApplicable(
                state, null, null, null, destKind, normalizedDestination, input.Port);
            if (platformRules.Any(static rule => !rule.Allowed))
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "blocked");
            NetworkAccessRule? explicitSystemRule = FindSystemExplicitRule(state.Rules, agentId, destKind, normalizedDestination, input.Port);
            if (explicitSystemRule is { Allowed: false })
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "blocked");
            NetworkAccessRule? seedRule = FindSystemSeedRule(policyStore.SeedRules, agentId, destKind, normalizedDestination, input.Port);
            if (explicitSystemRule is null && seedRule is { Allowed: false })
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "blocked");
            if (destKind == "ip" && NetworkAddressClassifier.IsNonPublicV4(normalizedDestination)
                && (platformRules.Any(static rule => rule.Allowed) || explicitSystemRule is { Allowed: true })
                && !platformRules.Any(static rule => rule.Allowed && rule.AllowLan) && explicitSystemRule is not { Allowed: true, AllowLan: true })
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "not_public");
            if (platformRules.Any(static rule => rule.Allowed))
                return RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, "rule_system",
                    allowLan: platformRules.Any(static rule => rule.Allowed && rule.AllowLan) || explicitSystemRule is { Allowed: true, AllowLan: true });
            if (explicitSystemRule is { Allowed: true })
            {
                return RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, "rule_system", allowLan: explicitSystemRule.AllowLan);
            }

            if (seedRule is not null && seedRule.Allowed)
            {
                return RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, "seed", allowLan: seedRule.AllowLan);
            }

            (string? sysReqId, bool sysOverflow) = policyStore.RecordOrUpdatePendingRequest(
                "system",
                agentId,
                connectorId: null,
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
            AgentId: userConnectorWithoutAgent ? null : agentId,
            ContainerId: origin,
            Kind: !string.IsNullOrEmpty(connectorId) ? "connector" : "agent-sandbox",
            ConnectorId: connectorId,
            ExecContext: "app",
            Egress: "normal",
            Attribution: userConnectorWithoutAgent ? "user" : "user-agent",
            CompatProfile: null,
            RegisteredUtc: now);

        ScopeMatchOutcome match = ResolveContainerScopeRule(state, syntheticEntry, destKind, normalizedDestination, input.Port);
        switch (match.Kind)
        {
            case ScopeMatchKind.Allowed:
                return RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, match.Reason,
                    openInternetBadge: match.OpenInternetBadge, allowLan: match.AllowLan);

            case ScopeMatchKind.DeniedNotPublic:
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "not_public");

            case ScopeMatchKind.BlockedByRule:
                policyStore.IncrementBlockedRequestAttemptInMemory(
                    userId,
                    userConnectorWithoutAgent ? null : agentId,
                    connectorId,
                    destKind,
                    normalizedDestination,
                    input.Port);
                return RecordOutboundDeny(now, userId, agentId, connectorId, normalizedDestination, input.Port, "blocked");

            default:
                if (input.Purpose == "provider-test" && input.CorrelationId is { Length: > 0 } token
                    && providerTests.TryGetValue(token, out ProviderTestGrant? grant)
                    && grant.ExpiresAt > timeProvider.GetUtcNow()
                    && grant.UserId == userId && grant.Host == normalizedDestination && grant.Port == input.Port
                    && (destKind != "ip" || !NetworkAddressClassifier.IsNonPublicV4(normalizedDestination) || grant.AllowLan))
                {
                    return RecordOutboundAllow(now, userId, agentId, connectorId, normalizedDestination, input.Port, "provider_test", allowLan: grant.AllowLan);
                }

                (string? reqId, bool overflow) = policyStore.RecordOrUpdatePendingRequest(
                    userId,
                    userConnectorWithoutAgent ? null : agentId,
                    connectorId,
                    attribution: userConnectorWithoutAgent ? "user" : "user-agent",
                    destKind,
                    normalizedDestination,
                    input.Port,
                    actorKind: connectorId is not null ? "connector" : "first-party",
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

    internal NetworkAccessDecisionResult EvaluateGatewayConnect(OutboundCheckInput input, bool publicOnly)
    {
        if (isContainerAuthorizationGateOpen?.Invoke() != true)
        {
            Interlocked.Increment(ref checks);
            return RecordOutboundDeny(timeProvider.GetUtcNow(), input.UserId, input.AgentId, input.ConnectorId, input.Host, input.Port, "topology_closed");
        }

        if (publicOnly && NetworkDestinationNormalizer.TryNormalizeIpv4(input.Host, out string normalized, out _)
            && NetworkAddressClassifier.IsNonPublicV4(normalized))
        {
            Interlocked.Increment(ref checks);
            return RecordOutboundDeny(timeProvider.GetUtcNow(), input.UserId, input.AgentId, input.ConnectorId, normalized, input.Port, "not_public");
        }

        return EvaluateOutboundCheck(input);
    }

    private ScopeMatchOutcome ResolveChannelProviderPermission(
        NetworkAccessPolicyState state, string destKind, string destination, int port)
    {
        bool slack = destination == "slack.com" || destination.EndsWith(".slack.com", StringComparison.Ordinal);
        if (destKind != "host" || port != 443 || (!slack && destination != "api.telegram.org"))
            return new(ScopeMatchKind.NoMatch, "channel_provider_denied", false, false);

        const string channelHost = "channelhost";
        string provider = slack ? "slack.com" : destination;
        NetworkAccessRule? exact = FindSystemExplicitRule(state.Rules, channelHost, destKind, destination, port);
        NetworkAccessRule? providerSwitch = provider == destination ? exact
            : FindSystemExplicitRule(state.Rules, channelHost, destKind, provider, port);
        IReadOnlyList<NetworkAccessRule> platform = NetworkAccessRuleMatcher.FindApplicable(state, null, null, null, destKind, destination, port);
        if (exact is { Allowed: false } || providerSwitch is { Allowed: false } || platform.Any(static rule => !rule.Allowed))
            return new(ScopeMatchKind.BlockedByRule, "blocked", false, false);
        if (exact is { Allowed: true } || providerSwitch is { Allowed: true })
            return new(ScopeMatchKind.Allowed, "rule_system", false, false);

        NetworkAccessRule? seed = FindSystemSeedRule(policyStore.SeedRules, channelHost, destKind, provider, port);
        if (seed is { Allowed: true })
            return new(ScopeMatchKind.Allowed, "seed", false, false);
        return new(ScopeMatchKind.BlockedByRule, "blocked", false, false);
    }

    private static bool HasPackageServiceExplicitDeny(
        NetworkAccessPolicyState state, RegistryEntry entry, string destKind, string destination, int port)
        => NetworkAccessRuleMatcher.FindApplicable(state, entry.UserId, entry.AgentId, entry.ConnectorId, destKind, destination, port)
            .Any(static rule => !rule.Allowed);
    private ScopeMatchOutcome ResolvePackageServiceDefault(
        NetworkAccessPolicyState state,
        string destKind,
        string destination,
        int port)
    {
        NetworkAccessRule? systemRule = FindExactRule(state.Rules, "system", null, null, null, destKind, destination, port)
            ?? policyStore.SeedRules.FirstOrDefault(rule =>
                string.Equals(rule.ScopeKind, "system", StringComparison.Ordinal)
                && string.Equals(rule.DestKind, destKind, StringComparison.Ordinal)
                && string.Equals(rule.Destination, destination, StringComparison.Ordinal)
                && rule.Port == port);
        return systemRule is { Allowed: true }
            ? new ScopeMatchOutcome(ScopeMatchKind.Allowed, "rule_system", systemRule.AllowLan, OpenInternetBadge: false)
            : new ScopeMatchOutcome(ScopeMatchKind.NoMatch, "no_rule", false, false);
    }

    private ScopeMatchOutcome ResolveContainerScopeRule(
        NetworkAccessPolicyState state,
        RegistryEntry entry,
        string destKind,
        string destination,
        int port)
    {
        IReadOnlyList<NetworkAccessRule> applicable = NetworkAccessRuleMatcher.FindApplicable(
            state, entry.UserId, entry.AgentId, entry.ConnectorId, destKind, destination, port,
            allowAnyPublic: entry.ExecContext is "isolated" or "isolated-with-agent-files");
        if (applicable.Any(static rule => !rule.Allowed))
            return new ScopeMatchOutcome(ScopeMatchKind.BlockedByRule, "blocked", false, false);

        NetworkAccessRule? allowed = applicable.FirstOrDefault(static rule => rule.Allowed && rule.AllowLan)
            ?? applicable.FirstOrDefault(static rule => rule.Allowed);
        if (allowed is not null)
        {
            if (destKind == "ip" && NetworkAddressClassifier.IsNonPublicV4(destination) && !allowed.AllowLan
                && !IsPackageProxyService(destKind, destination, port))
                return new ScopeMatchOutcome(ScopeMatchKind.DeniedNotPublic, "not_public", false, false);
            string reason = allowed.DestKind == "any-public" ? "any_public"
                : allowed.ScopeKind == "user" && allowed.ConnectorId is not null ? "rule_user_connector"
                : "rule_" + allowed.ScopeKind;
            return new ScopeMatchOutcome(ScopeMatchKind.Allowed, reason, allowed.AllowLan,
                OpenInternetBadge: allowed.DestKind == "any-public");
        }

        if (entry.UserId == "system" && entry.AgentId == "package-proxy"
            && policyStore.SeedRules.Any(static rule => rule.ScopeKind == "agent"
                && rule.UserId == "system" && rule.AgentId == "package-proxy"
                && rule.DestKind == "any-public" && rule.Allowed))
        {
            if (destKind == "ip" && NetworkAddressClassifier.IsNonPublicV4(destination))
                return new ScopeMatchOutcome(ScopeMatchKind.DeniedNotPublic, "not_public", false, false);
            return new ScopeMatchOutcome(ScopeMatchKind.Allowed, "seed", false, true);
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

    private static string GetDecisionClusterName(
        bool isTls,
        bool allowLan,
        string destKind,
        string destination,
        int port)
        => IsPackageProxyService(destKind, destination, port)
            ? ClusterPackageProxy
            : GetClusterName(isTls, allowLan);

    private static bool IsPackageProxyService(string destKind, string destination, int port)
        => string.Equals(destKind, "ip", StringComparison.Ordinal)
            && string.Equals(destination, NetworkAddressClassifier.PackageProxyServiceAddress, StringComparison.Ordinal)
            && port == NetworkAddressClassifier.PackageProxyServicePort;

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
        bool openInternetBadge = false,
        bool allowLan = false)
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
        return new NetworkAccessDecisionResult(true, reason, Cluster: null, RequestId: null, openInternetBadge) { AllowLan = allowLan };
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
