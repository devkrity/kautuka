using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class NetworkCapacityTests
{
    [Fact]
    public void CapacityIsSharedAndOccupancyRemainsUnknownUntilRecovery()
    {
        string path = Path.Combine(Path.GetTempPath(), "kautuka-capacity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Policy reads must not bootstrap the runtime."));
            using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, new RuntimeImageAllowlist(1, []), path);
            NetworkAccessPolicyView admin = runtime.GetNetworkAccessPolicy(new NetworkAccessActor("admin", true));
            Assert.Equal(22, admin.MaxPrivateNetworks);
            Assert.Equal(-1, admin.ActivePrivateNetworks);
            NetworkAccessPolicyView user = runtime.GetNetworkAccessPolicy(new NetworkAccessActor("user", false));
            Assert.Equal(0, user.MaxPrivateNetworks);
            Assert.Equal(-1, user.ActivePrivateNetworks);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
}
