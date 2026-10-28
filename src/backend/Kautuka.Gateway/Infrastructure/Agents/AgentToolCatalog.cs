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
    IPlatformConfigurationProvider configProvider) : IAgentToolCatalog
{
    private readonly IAgentToolProvider[] providers = [.. providers];
    private readonly IAgentToolSurfaceStore surfaceStore = surfaceStore;
    private readonly IPlatformConfigurationProvider configProvider = configProvider;
    private readonly object sync = new();
    private readonly Dictionary<string, Task<AgentToolSurfaceContribution>> inflight = new(StringComparer.Ordinal);

    public async ValueTask<IReadOnlyList<AIFunction>> BuildToolsAsync(AgentToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        string fingerprint = await FingerprintAsync(context, ct).ConfigureAwait(false);
        AgentToolSurfaceSnapshot? snapshot = surfaceStore.TryGet(context.AgentId, fingerprint);
        if (snapshot is not null)
        {
            return await HydrateAsync(context, snapshot.Catalogue.Entries, ct).ConfigureAwait(false);
        }

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
        return await HydrateAsync(context, contribution.Entries, ct).ConfigureAwait(false);
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
        string configFingerprint = AgentToolingFingerprint.Compute(context.AgentDefinition, configProvider.GetCurrent());
        if (string.IsNullOrEmpty(context.UserId))
        {
            return configFingerprint;
        }

        List<(string ConnectorId, string Endpoint)> endpoints = [];
        foreach (IAgentToolProvider provider in providers)
        {
            if (provider is McpAgentToolProvider mcp)
            {
                endpoints = await mcp.DescribeCallerEndpointsAsync(context, ct).ConfigureAwait(false);
                break;
            }
        }

        return AgentToolingFingerprint.WithCaller(configFingerprint, context.UserId, endpoints);
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
