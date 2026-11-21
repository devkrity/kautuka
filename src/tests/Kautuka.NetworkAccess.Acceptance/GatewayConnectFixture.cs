using System.Runtime.InteropServices;

using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using NacController = Kautuka.NetworkAccessController.NetworkAccessController;

namespace Kautuka.NetworkAccess.Acceptance;

internal static class GatewayConnectFixture
{
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "envoy.yaml");
        string config = BoundaryRulesetRenderer.RenderEnvoyConfig(includeGatewayConnect: true);
        await File.WriteAllTextAsync(configPath, config).ConfigureAwait(false);

        using var policy = new NetworkAccessPolicyStore(Path.Combine(directory, "fixture-policy.json"));
        var admin = new NetworkAccessActor("fixture-admin", IsAdmin: true);
        foreach ((string host, bool allowLan) in new[]
        {
            ("fixture-public.test", false),
            ("fixture-public-to-lan.test", false),
            ("fixture-lan.test", true),
            ("fixture-lan-to-loopback.test", true),
        })
        {
            policy.SetRule(admin, new RuleWriteSpec("agent", "fixture-user", "fixture-agent", null, host, "host", 18080,
                Allowed: true, AllowLan: allowLan), policy.Revision);
        }

        var controller = new NacController(policy, new WorkloadRegistry(), isContainerAuthorizationGateOpen: static () => true);
        var server = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
        {
            SocketPath = Path.Combine(directory, "envoy.sock"),
            LockFilePath = Path.Combine(directory, "fixture.lock"),
        });
        await using var serverLifetime = server.ConfigureAwait(false);
        await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine("Gateway CONNECT authorization fixture ready");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopped.TrySetResult();
        });
        await stopped.Task.ConfigureAwait(false);
    }
}
