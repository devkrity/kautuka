using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Discovery;
using Kautuka.UpdateService.State;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

public static class UpdateServiceCollectionExtensions
{
    public static IServiceCollection AddUpdateService(this IServiceCollection services, bool applianceMode = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<UpdateOptions>()
            .BindConfiguration(UpdateOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<UpdateOptions>, UpdateOptionsValidation>();

        IHttpClientBuilder httpClient = services.AddHttpClient("kautuka.updateservice.github");
        if (applianceMode)
        {
            httpClient.ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { UseProxy = false });
            services.AddHttpClient(PublicSuffixRefreshWorker.ClientName, static client => client.MaxResponseContentBufferSize = 4 * 1024 * 1024)
                .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            services.AddHostedService<PublicSuffixRefreshWorker>();
        }
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
