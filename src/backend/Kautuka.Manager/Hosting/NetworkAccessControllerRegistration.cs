namespace Kautuka.Manager.Hosting;

/// <summary>Registers the Manager-owned NAC decision controller with its live topology gate.</summary>
public static class NetworkAccessControllerRegistration
{
    public static IServiceCollection AddManagerNetworkAccessController(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp => new Kautuka.NetworkAccessController.NetworkAccessController(
            sp.GetRequiredService<Kautuka.NetworkAccessController.NetworkAccessPolicyStore>(),
            sp.GetRequiredService<Kautuka.NetworkAccessController.IWorkloadRegistry>(),
            sp.GetRequiredService<Kautuka.NetworkAccessController.NetworkAccessAuditRing>(),
            isContainerAuthorizationGateOpen: () =>
                sp.GetRequiredService<Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.EgressTopologyManager>().IsStartupGateOpen));
        return services;
    }
}
