using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

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
