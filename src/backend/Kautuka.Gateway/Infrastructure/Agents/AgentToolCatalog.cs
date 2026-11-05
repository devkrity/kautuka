using Kautuka.Gateway.Domain.Connectors;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal interface IAgentToolProvider
{
    ValueTask<IEnumerable<AIFunction>> BuildToolsAsync(AgentToolContext context, CancellationToken ct);
}

/// <summary>
/// Catalogue path for a tool provider. Miss persists entries without session-bound
/// functions; hit hydrates those entries for the current tool context.
/// Cheap providers can keep the defaults (rebuild live tools).
/// </summary>
internal interface IAgentToolSurfaceContributor : IAgentToolProvider
{
    string SurfaceSource { get; }

    async ValueTask<AgentToolSurfaceContribution> ContributeAsync(AgentToolContext context, CancellationToken ct)
    {
        IReadOnlyList<AIFunction> tools = [.. await BuildToolsAsync(context, ct).ConfigureAwait(false)];
        return new AgentToolSurfaceContribution(
            [.. tools.Select(tool => AgentToolSurfaceEntry.From(SurfaceSource, tool.Name, tool))],
            Complete: true);
    }

    async ValueTask<IReadOnlyList<AIFunction>> HydrateAsync(
        AgentToolContext context,
        IReadOnlyList<AgentToolSurfaceEntry> entries,
        CancellationToken ct)
    {
        _ = entries;
        return [.. await BuildToolsAsync(context, ct).ConfigureAwait(false)];
    }
}

internal interface IAgentToolCatalog
{
    ValueTask<IReadOnlyList<AIFunction>> BuildToolsAsync(AgentToolContext context, CancellationToken ct);
}

internal sealed class AgentToolCatalog(
    IEnumerable<IAgentToolProvider> providers,
    IAgentToolSurfaceStore surfaceStore,
    IPlatformConfigurationProvider configProvider,
    RemoteMcpCredentialRevisionTracker? credentialRevisions = null) : IAgentToolCatalog
{
    private readonly IAgentToolProvider[] providers = [.. providers];
    private readonly IAgentToolSurfaceStore surfaceStore = surfaceStore;
    private readonly IPlatformConfigurationProvider configProvider = configProvider;
    private readonly RemoteMcpCredentialRevisionTracker credentialRevisions = credentialRevisions ?? new();
    private readonly object sync = new();
    private readonly Dictionary<string, Task<AgentToolSurfaceContribution>> inflight = new(StringComparer.Ordinal);

    public async ValueTask<IReadOnlyList<AIFunction>> BuildToolsAsync(AgentToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            string fingerprint = await FingerprintAsync(context, ct).ConfigureAwait(false);
            AgentDefinition? currentAgent = configProvider.GetCurrent().Agents.FirstOrDefault(agent => string.Equals(agent.Id, context.AgentId, StringComparison.Ordinal));
            if (currentAgent is null || !currentAgent.Enabled || !GatewayOwnershipAccess.CallerCanUse(context.UserId, currentAgent.Scope, currentAgent.OwnerUserId))
                throw new InvalidOperationException("Agent is no longer available to this caller.");
            context = context with { AgentDefinition = currentAgent };
            AgentToolSurfaceSnapshot? snapshot = surfaceStore.TryGet(context.AgentId, fingerprint);
            IReadOnlyList<AgentToolSurfaceEntry> entries;
            if (snapshot is not null)
            {
                entries = snapshot.Catalogue.Entries;
            }
            else
            {
                string flightKey = string.Concat(context.AgentId, "\n", fingerprint);
                Task<AgentToolSurfaceContribution> pending;
                lock (sync)
                {
                    if (!inflight.TryGetValue(flightKey, out pending!))
                    {
                        pending = MaterializeMissAsync(context, fingerprint);
                        inflight[flightKey] = pending;
                        ObserveInflight(flightKey, pending);
                    }
                }
                AgentToolSurfaceContribution contribution = await pending.WaitAsync(ct).ConfigureAwait(false);
                entries = contribution.Entries;
            }
            IReadOnlyList<AIFunction> tools = await HydrateAsync(context, entries, ct).ConfigureAwait(false);
            if (string.Equals(fingerprint, await FingerprintAsync(context, ct).ConfigureAwait(false), StringComparison.Ordinal))
                return tools;
        }
        throw new InvalidOperationException("Connector configuration changed while building tools. Retry the operation.");
    }

    private void ObserveInflight(string flightKey, Task<AgentToolSurfaceContribution> pending)
    {
        _ = pending.ContinueWith(
            static (task, state) =>
            {
                (AgentToolCatalog catalog, string key) = ((AgentToolCatalog catalog, string key))state!;
                lock (catalog.sync)
                {
                    if (catalog.inflight.TryGetValue(key, out Task<AgentToolSurfaceContribution>? current)
                        && ReferenceEquals(current, task))
                    {
                        catalog.inflight.Remove(key);
                    }
                }
            },
            (this, flightKey),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<string> FingerprintAsync(AgentToolContext context, CancellationToken ct)
    {
        PlatformConfiguration config = configProvider.GetCurrent();
        AgentDefinition? currentAgent = config.Agents.FirstOrDefault(agent => string.Equals(agent.Id, context.AgentId, StringComparison.Ordinal));
        if (currentAgent is null || !currentAgent.Enabled || !GatewayOwnershipAccess.CallerCanUse(context.UserId, currentAgent.Scope, currentAgent.OwnerUserId))
            throw new InvalidOperationException("Agent is no longer available to this caller.");
        context = context with { AgentDefinition = currentAgent };
        string configFingerprint = AgentToolingFingerprint.Compute(currentAgent, config);
        List<(string ConnectorId, string Endpoint)> endpoints = [];
        foreach (IAgentToolProvider provider in providers)
        {
            if (provider is McpAgentToolProvider mcp)
            {
                endpoints = await mcp.DescribeCallerEndpointsAsync(context, ct).ConfigureAwait(false);
                break;
            }
        }

        PlatformConfiguration current = configProvider.GetCurrent();
        var revisions = new List<(string ConnectorId, string Revision)>();
        foreach ((string connectorId, string endpoint) in endpoints)
        {
            McpServerDefinition? server = current.McpServers.FirstOrDefault(item => string.Equals(item.Id, connectorId, StringComparison.Ordinal));
            if (server is null) continue;
            bool personal = !string.Equals(server.HttpUrl?.AbsoluteUri, endpoint, StringComparison.Ordinal);
            bool includeSystem = !personal && !string.Equals(server.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase);
            revisions.Add((connectorId, credentialRevisions.Get(connectorId, context.UserId ?? string.Empty, includeSystem)));
        }
        return AgentToolingFingerprint.WithCaller(configFingerprint, context.UserId, endpoints, revisions);
    }

    private async Task<AgentToolSurfaceContribution> MaterializeMissAsync(AgentToolContext context, string fingerprint)
    {
        var entries = new List<AgentToolSurfaceEntry>();
        bool complete = true;
        foreach (IAgentToolProvider provider in providers)
        {
            AgentToolSurfaceContribution contribution = await AsContributor(provider)
                .ContributeAsync(context, CancellationToken.None)
                .ConfigureAwait(false);
            complete &= contribution.Complete;
            entries.AddRange(contribution.Entries);
        }

        var combined = new AgentToolSurfaceContribution(entries, complete);
        if (complete)
        {
            surfaceStore.Save(
                context.AgentId,
                fingerprint,
                new AgentToolSurfaceCatalogue { Entries = [.. entries] });
        }

        return combined;
    }

    private async ValueTask<IReadOnlyList<AIFunction>> HydrateAsync(
        AgentToolContext context,
        IReadOnlyList<AgentToolSurfaceEntry> entries,
        CancellationToken ct)
    {
        var all = new List<AIFunction>();
        foreach (IAgentToolProvider provider in providers)
        {
            all.AddRange(await AsContributor(provider).HydrateAsync(context, entries, ct).ConfigureAwait(false));
        }

        return all;
    }

    private static IAgentToolSurfaceContributor AsContributor(IAgentToolProvider provider)
        => provider as IAgentToolSurfaceContributor
            ?? throw new InvalidOperationException($"{provider.GetType().Name} must implement {nameof(IAgentToolSurfaceContributor)}.");
}
