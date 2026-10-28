using Kautuka.Gateway.RuntimePlane.Configuration;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kautuka.Gateway.RuntimePlane;

internal static class RuntimePlaneServiceCollectionExtensions
{
    public static IServiceCollection AddKautukaRuntimePlane(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ConfigCaptureGate>();
        services.AddSingleton<IConfigSnapshotStore, SqliteConfigSnapshotStore>();
        services.AddSingleton<IConfigSnapshotCapture, GatewayConfigSnapshotCapture>();
        return services;
    }
}
