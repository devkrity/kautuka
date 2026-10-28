// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Runtime/RuntimeInspector.cs | Namespace: Kautuka.Tui.Features.Runtime
using System.Diagnostics.CodeAnalysis;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

namespace Kautuka.Tui.Features.Runtime;

public sealed class RuntimeInspector
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Keeps a consistent instance-based inspector surface in the TUI.")]
    public async Task<Result<RuntimeSummary>> GetAsync(TuiCommandOptions? options, CancellationToken cancellationToken)
    {
        using GatewayApiClient client = GatewaySessionFactory.CreateClient(options);
        Result<GatewayRuntimeContainersResponse> responseResult = await client.GetRuntimeContainersAsync(cancellationToken).ConfigureAwait(false);
        if (!responseResult.IsSuccess || responseResult.Value is null)
        {
            return Results.Failure<RuntimeSummary>(responseResult.ErrorMessage ?? "Failed to inspect the runtime.");
        }

        GatewayRuntimeContainersResponse response = responseResult.Value;
        if (!response.Ok)
        {
            return Results.Success(new RuntimeSummary(false, response.Message, []));
        }

        IReadOnlyList<ContainerStatus> containers =
        [
            .. response.Containers.Select(static container => new ContainerStatus(container.Name, container.Image, container.Status))
        ];

        string summary = containers.Count == 0
            ? response.Message
            : $"{containers.Count} container(s) discovered.";

        return Results.Success(new RuntimeSummary(true, summary, containers));
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Keeps a consistent instance-based inspector surface in the TUI.")]
    public async Task<Result<PackageCacheSummary>> GetPackageCacheAsync(TuiCommandOptions? options, CancellationToken cancellationToken)
    {
        using GatewayApiClient client = GatewaySessionFactory.CreateClient(options);
        Result<GatewayPkgsProxyCacheStatsResponse> responseResult = await client.GetPkgsProxyCacheStatsAsync(cancellationToken).ConfigureAwait(false);
        if (!responseResult.IsSuccess || responseResult.Value is null)
        {
            return Results.Failure<PackageCacheSummary>(responseResult.ErrorMessage ?? "Failed to read package proxy cache stats.");
        }

        GatewayPkgsProxyCacheStatsResponse response = responseResult.Value;
        return Results.Success(new PackageCacheSummary(
            response.Ok,
            response.Message,
            response.Packages,
            response.Versions,
            response.Artifacts,
            response.TotalSizeBytes,
            response.TotalHits,
            response.Ecosystems,
            response.Popular,
            response.Recent));
    }
}

public sealed record RuntimeSummary(bool IsAvailable, string Summary, IReadOnlyList<ContainerStatus> Containers);

public sealed record ContainerStatus(string Name, string Image, string Status);

public sealed record PackageCacheSummary(
    bool Ok,
    string Message,
    long Packages,
    long Versions,
    long Artifacts,
    long TotalSizeBytes,
    long TotalHits,
    IReadOnlyDictionary<string, long> Ecosystems,
    IReadOnlyList<GatewayPkgsProxyPopularPackage> Popular,
    IReadOnlyList<GatewayPkgsProxyRecentPackage> Recent);
