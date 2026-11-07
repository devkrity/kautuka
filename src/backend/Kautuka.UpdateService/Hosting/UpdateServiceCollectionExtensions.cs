using Kautuka.Hosting.Hosting;
using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Discovery;
using Kautuka.UpdateService.State;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

public static class UpdateServiceCollectionExtensions
{
    public static IServiceCollection AddUpdateService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<UpdateOptions>()
            .BindConfiguration(UpdateOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<UpdateOptions>, UpdateOptionsValidation>();

        services.AddHttpClient("kautuka.updateservice.github").ConfigurePrimaryHttpMessageHandler(sp =>
            KautukaArtifactHttpTransport.CreateForConfiguration(sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
        services.AddSingleton<IUpdateRuntimePolicyStore, UpdateRuntimePolicyStore>();
        services.AddSingleton<IUpdateStateStore, JsonUpdateStateStore>();
        services.AddSingleton<IHostedCatalogStateStore, JsonHostedCatalogStateStore>();
        services.AddSingleton<IHostedCatalogService, HostedCatalogService>();
        services.AddSingleton<UpdateOperationHistoryStore>();
        services.AddSingleton<IReleaseDiscoveryClient, ManifestUrlReleaseDiscoveryClient>();
        services.AddSingleton<IUpdateCoordinator, UpdateCoordinator>();
        services.AddHostedService<UpdateActivationWorker>();
        services.AddHostedService<UpdateDiscoveryWorker>();
        services.AddHostedService<HostedCatalogRefreshWorker>();

        return services;
    }
}
