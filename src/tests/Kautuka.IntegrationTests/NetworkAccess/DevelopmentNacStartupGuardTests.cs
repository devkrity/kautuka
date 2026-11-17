using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class DevelopmentNacStartupGuardTests
{
    [Fact]
    public async Task DevelopmentWithClosedGateFailsManagerStartupAsync()
    {
        var guard = CreateGuard(isOpen: false, Environments.Development);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => guard.StartAsync(CancellationToken.None));

        Assert.Contains("NAC startup gate is closed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevelopmentWithOpenGateStartsAsync()
    {
        var guard = CreateGuard(isOpen: true, Environments.Development);

        await guard.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DevelopmentWithPendingBrakeRetryStartsManagerWithoutOpeningLaunchGateAsync()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"nac-dev-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var podman = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(
            0, string.Empty, string.Empty, ProcessTerminationReason.Completed)));
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman,
            new WorkloadAddressAllocator(Path.Combine(tempDirectory, "cursor.json")),
            new WorkloadPostStartVerifier(podman), registry, tempDirectory);
        typeof(EgressTopologyManager).GetMethod("QueueStartupCanaryRetry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(manager, [registry]);
        var guard = new DevelopmentNacStartupGuard(
            manager, new TestHostEnvironment(Environments.Development), NullLogger<DevelopmentNacStartupGuard>.Instance);

        await guard.StartAsync(CancellationToken.None);

        Assert.False(manager.IsStartupGateOpen);
        Assert.True(manager.IsStartupRetryPending);
        manager.CloseLaunchAdmission();
        Assert.False(manager.IsStartupRetryPending);
        await manager.DisposeAsync();
        Directory.Delete(tempDirectory, recursive: true);
    }

    [Fact]
    public async Task ProductionWithClosedGatePreservesFailClosedAvailabilityAsync()
    {
        var guard = CreateGuard(isOpen: false, Environments.Production);

        await guard.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ClosedDevelopmentGateAbortsHostBeforeLaterBindingServiceStartsAsync()
    {
        var startupState = new MutableStartupState();
        var topologyInitializer = new TopologyInitializerProbe(startupState);
        var bindingService = new BindingServiceProbe();
        using IHost host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
                services.AddSingleton<IWorkloadStartupState>(startupState);
                services.AddSingleton<ILogger<DevelopmentNacStartupGuard>>(NullLogger<DevelopmentNacStartupGuard>.Instance);
                services.AddSingleton<IHostedService>(topologyInitializer);
                services.AddSingleton<IHostedService, DevelopmentNacStartupGuard>();
                services.AddSingleton<IHostedService>(bindingService);
            })
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.True(topologyInitializer.Started);
        Assert.False(bindingService.Started);
    }

    private static DevelopmentNacStartupGuard CreateGuard(bool isOpen, string environmentName)
        => new(new StartupState(isOpen), new TestHostEnvironment(environmentName), NullLogger<DevelopmentNacStartupGuard>.Instance);

    private sealed class StartupState(bool isOpen) : IWorkloadStartupState
    {
        public bool IsStartupGateOpen { get; } = isOpen;
    }

    private sealed class MutableStartupState : IWorkloadStartupState
    {
        public bool IsStartupGateOpen { get; set; }
    }

    private sealed class TopologyInitializerProbe(MutableStartupState startupState) : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            startupState.IsStartupGateOpen = false;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BindingServiceProbe : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Kautuka.Manager.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
