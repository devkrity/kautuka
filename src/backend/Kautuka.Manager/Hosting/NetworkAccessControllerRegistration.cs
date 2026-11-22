namespace Kautuka.Manager.Hosting;

/// <summary>Registers the Manager-owned NAC decision controller with its live topology gate.</summary>
public static class NetworkAccessControllerRegistration
{
    public static IServiceCollection AddManagerNetworkAccessController(this IServiceCollection services, TimeSpan approvalHold = default,
        int maxHeldCalls = Kautuka.NetworkAccessController.NetworkAccessController.DefaultHeldCallLimit,
        int maxHeldCallsPerUser = Kautuka.NetworkAccessController.NetworkAccessController.DefaultHeldCallLimitPerUser)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp => new Kautuka.NetworkAccessController.NetworkAccessController(
            sp.GetRequiredService<Kautuka.NetworkAccessController.NetworkAccessPolicyStore>(),
            sp.GetRequiredService<Kautuka.NetworkAccessController.IWorkloadRegistry>(),
            sp.GetRequiredService<Kautuka.NetworkAccessController.NetworkAccessAuditRing>(),
            isContainerAuthorizationGateOpen: () =>
                sp.GetRequiredService<Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.EgressTopologyManager>().IsStartupGateOpen,
            packageProxyAdmission: () =>
                sp.GetRequiredService<Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.EgressTopologyManager>().GetPackageProxyAdmission(),
            approvalHold: approvalHold,
            maxHeldCalls: maxHeldCalls,
            maxHeldCallsPerUser: maxHeldCallsPerUser));
        return services;
    }
}
