using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceSidecarSupervisorReadinessTests
{
    [Fact]
    public void BuildRunArgumentsUsesSandboxNetworkAliasForServiceDiscovery()
    {
        WorkspaceSidecarDefinition sidecar = new(
            Name: "kautuka-pkgs-proxy",
            Image: "localhost/kautuka-pkgs-proxy:local",
            Ports: [],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: false);

        List<string> arguments = InvokeBuildRunArguments(sidecar);

        int networkIndex = arguments.IndexOf("--network");
        Assert.True(networkIndex >= 0);
        Assert.Equal("kautuka-sandbox", arguments[networkIndex + 1]);

        int aliasIndex = arguments.IndexOf("--network-alias");
        Assert.True(aliasIndex >= 0);
        Assert.Equal("kautuka-pkgs-proxy", arguments[aliasIndex + 1]);
    }

    [Fact]
    public void BuildRunArgumentsPublishesOptionalHostBindAddress()
    {
        WorkspaceSidecarDefinition sidecar = new(
            Name: "kautuka-openobserve",
            Image: "public.ecr.aws/zinclabs/openobserve:latest",
            Ports:
            [
                new WorkspaceSidecarPortBinding(20215, 5080, "127.0.0.1")
            ],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: false);

        List<string> arguments = InvokeBuildRunArguments(sidecar);

        int publishIndex = arguments.IndexOf("-p");
        Assert.True(publishIndex >= 0);
        Assert.Equal("127.0.0.1:20215:5080", arguments[publishIndex + 1]);
        int networkIndex = arguments.IndexOf("--network");
        Assert.True(networkIndex >= 0);
        Assert.Equal("kautuka-telemetry", arguments[networkIndex + 1]);
    }

    [Theory]
    [InlineData("kautuka-pkgs-proxy", ":U")]
    [InlineData("kautuka-openobserve", "")]
    public void BuildRunArgumentsUsesPodmanUFlagOnlyForSidecarsRequiringOwnershipChanges(string name, string writableSuffix)
    {
        WorkspaceSidecarDefinition sidecar = new(
            Name: name,
            Image: "localhost/kautuka-pkgs-proxy:local",
            Ports: [],
            Volumes:
            [
                new WorkspaceSidecarVolumeBinding("/var/lib/kautuka/proxy/pkgs/data", "/data"),
                new WorkspaceSidecarVolumeBinding("/var/lib/kautuka/proxy/pkgs/conf", "/conf", ReadOnly: true)
            ],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: false);

        List<string> arguments = InvokeBuildRunArguments(sidecar);

        List<string> volumeSpecs = [];
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == "-v")
            {
                volumeSpecs.Add(arguments[i + 1]);
            }
        }

        Assert.Contains("/var/lib/kautuka/proxy/pkgs/data:/data" + writableSuffix, volumeSpecs);
        Assert.Contains("/var/lib/kautuka/proxy/pkgs/conf:/conf:ro", volumeSpecs);
    }

    [Fact]
    public async Task EnsureReadyAsyncReturnsTrueWhenOnlyOptionalSidecarFailsAsync()
    {
        WorkspaceSidecarSupervisor supervisor = CreateSupervisor(new WorkspaceSidecarDefinition(
            Name: "optional-sidecar",
            Image: "docker.io/example/optional:latest",
            Ports: [],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: false,
            Policy: "always"));

        bool ready = await supervisor.EnsureReadyAsync(CreateRuntimeSettings(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(ready);
    }

    [Fact]
    public async Task EnsureReadyAsyncReturnsFalseWhenRequiredSidecarFailsAsync()
    {
        string sidecarName = $"required-sidecar-{Guid.NewGuid():N}";
        WorkspaceSidecarSupervisor supervisor = CreateSupervisor(new WorkspaceSidecarDefinition(
            Name: sidecarName,
            Image: "docker.io/example/required:latest",
            Ports: [],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: true,
            Policy: "always"));

        bool ready = await supervisor.EnsureReadyAsync(CreateRuntimeSettings(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(ready);
    }

    private static WorkspaceSidecarSupervisor CreateSupervisor(WorkspaceSidecarDefinition sidecar)
    {
        ServiceCollection services = new();
        services.AddHttpClient("manager.sidecars");

        using ServiceProvider provider = services.BuildServiceProvider();

        return new WorkspaceSidecarSupervisor(
            new WorkspaceSidecarCatalog([sidecar]),
            provider.GetRequiredService<IHttpClientFactory>(),
            new PodmanCommandRunner(),
            Options.Create(new ManagerContainerStartupOptions()),
            NullLogger<WorkspaceSidecarSupervisor>.Instance);
    }

    private static ManagerRuntimeSettings CreateRuntimeSettings()
        => new(
            new ManagerWorkspaceDefinition
            {
                BaseRoot = "/tmp/kautuka-workspaces",
            },
            new ManagerSandboxRuntimeDefinition
            {
                Image = "kautuka-base:latest",
                DefaultTimeout = 120,
            },
            new WorkspaceTelemetryDefinition());

    private static List<string> InvokeBuildRunArguments(WorkspaceSidecarDefinition sidecar)
    {
        MethodInfo method = typeof(WorkspaceSidecarSupervisor)
            .GetMethod("BuildRunArguments", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildRunArguments method was not found.");

        object? result = method.Invoke(null, [sidecar]);
        return Assert.IsType<List<string>>(result);
    }
}
