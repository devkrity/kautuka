using Kautuka.Control.Core.Config;

namespace Kautuka.Control.Core.Services;

/// <summary>
/// Creates the correct IServiceManager for the given ServiceMode.
/// No reflection — explicit switch expression.
/// </summary>
public static class ServiceManagerFactory
{
    public static IServiceManager Create(ServiceMode mode) => mode switch
    {
        ServiceMode.Nssm => new NssmServiceManager(),
        ServiceMode.SystemdUser => new SystemdServiceManager(userMode: true),
        ServiceMode.SystemdSystem => new SystemdServiceManager(userMode: false),
        ServiceMode.LaunchdAgent => new LaunchdServiceManager(agentMode: true),
        ServiceMode.LaunchdDaemon => new LaunchdServiceManager(agentMode: false),
        ServiceMode.Sidecar => new SidecarServiceManager(),
        _ => new SidecarServiceManager(),
    };
}
