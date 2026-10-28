using System.ComponentModel;
using System.Globalization;

using Kautuka.Manager.RuntimeContainer.Configuration;

using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed class PkgsProxyCacheDiagnosticsService(
    PodmanCommandRunner podmanCommandRunner,
    WorkspaceSidecarCatalog sidecarCatalog,
    ILogger<PkgsProxyCacheDiagnosticsService> logger)
{
    public const string SidecarName = "kautuka-pkgs-proxy";
    public const int ExecTimeoutSeconds = 15;

    private readonly PodmanCommandRunner podmanCommandRunner = podmanCommandRunner;
    private readonly WorkspaceSidecarCatalog sidecarCatalog = sidecarCatalog;
    private readonly ILogger<PkgsProxyCacheDiagnosticsService> logger = logger;

    public async Task<PkgsProxyCacheStatsResult> GetCacheStatsAsync(CancellationToken cancellationToken)
    {
        if (!sidecarCatalog.Sidecars.Any(static sidecar =>
                string.Equals(sidecar.Name, SidecarName, StringComparison.Ordinal)))
        {
            return new PkgsProxyCacheStatsResult(false, "Package proxy sidecar is not in the catalog.", null);
        }

        try
        {
            PodmanCommandResult result = await podmanCommandRunner.RunAsync(
                [
                    "exec",
                    SidecarName,
                    "proxy",
                    "stats",
                    "-json",
                    "-popular",
                    PkgsProxyCacheStatsParser.PackageListLimit.ToString(CultureInfo.InvariantCulture),
                    "-recent",
                    PkgsProxyCacheStatsParser.PackageListLimit.ToString(CultureInfo.InvariantCulture),
                ],
                ExecTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                return new PkgsProxyCacheStatsResult(
                    false,
                    PodmanFailureFormatter.FormatFailure("Read package proxy cache stats", result),
                    null);
            }

            if (!PkgsProxyCacheStatsParser.TryParse(result.StdOut, out PkgsProxyCacheStatsSnapshot? stats, out string? error))
            {
                return new PkgsProxyCacheStatsResult(false, error, null);
            }

            return new PkgsProxyCacheStatsResult(true, "Package proxy cache stats.", stats);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception)
        {
            logger.LogWarning(ex, "Failed to read package proxy cache stats.");
            return new PkgsProxyCacheStatsResult(false, "Package proxy cache diagnostics unavailable.", null);
        }
    }
}
