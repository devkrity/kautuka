namespace Kautuka.Gateway.Infrastructure.DependencyInjection;

using Kautuka.Gateway.Domain.Stores;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKautukaPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<KautukaDatabase>();
        services.AddSingleton<SqliteAgentStoreDatabase>();
        services.AddSingleton<IAgentDefinitionStore, SqliteAgentDefinitionStore>();
        services.AddSingleton<IAgentRuntimeSessionRepository, SqliteAgentRuntimeSessionRepository>();
        services.AddSingleton<IAgentRuntimeRunRepository, SqliteAgentRuntimeRunRepository>();
        services.AddSingleton<IAgentMemoryFactRepository, SqliteAgentMemoryFactRepository>();
        services.AddSingleton<IAgentContinuationCheckpointRepository, SqliteAgentContinuationCheckpointRepository>();
        services.AddSingleton<SqlitePlatformConfigurationProvider>();
        services.AddSingleton<IPlatformConfigurationProvider>(static serviceProvider =>
            serviceProvider.GetRequiredService<SqlitePlatformConfigurationProvider>());
        services.AddSingleton<IProviderTypeCatalog, SqliteProviderTypeCatalog>();
        services.AddSingleton<ITopologyConfigurationWriter, SqliteTopologyConfigurationWriter>();
        services.AddSingleton<IGatewayUserCommandRepository, SqliteGatewayUserCommandRepository>();
        services.AddSingleton<ServiceTopologyProjector>();

        return services;
    }
}
