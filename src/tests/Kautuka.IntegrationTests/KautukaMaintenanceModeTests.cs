using Kautuka.Hosting.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class KautukaMaintenanceModeTests
{
    [Theory]
    [InlineData("{\"phase\":\"verifying\"}", true)]
    [InlineData("{\"phase\":\"starting\"}", true)]
    [InlineData("{\"phase\":\"committed\"}", false)]
    [InlineData("{\"phase\":\"completed\"}", false)]
    [InlineData("{\"phase\":\"aborted\"}", false)]
    [InlineData("[]", true)]
    [InlineData("invalid", true)]
    public void WorkersStartBehindTheFenceOnlyForTheFinalStart(string marker, bool expected)
        => Assert.Equal(expected, KautukaMaintenanceMode.ShouldSuppressWorkers(marker));

    [Theory]
    [InlineData("starting", 0)]
    [InlineData("committed", 1)]
    public async Task QueuedOutboundWorkRunsOnlyAfterCommit(string phase, int expectedDeliveries)
    {
        var counter = new DispatchCounter();
        var services = new ServiceCollection();
        services.AddSingleton(counter);
        services.AddHostedService<QueuedOutboundWorker>();
        KautukaMaintenanceMode.SuppressApplicationHostedServices(
            services, KautukaMaintenanceMode.ShouldSuppressWorkers($"{{\"phase\":\"{phase}\"}}"));

        await using ServiceProvider provider = services.BuildServiceProvider();
        foreach (IHostedService worker in provider.GetServices<IHostedService>())
            await worker.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(expectedDeliveries, counter.Deliveries);
    }

    [Fact]
    public void WriterLeaseIsOptionalForDevelopmentSourceRuns()
    {
        Assert.Null(KautukaDatabaseWriterLease.AcquireForMode("dev"));
        Assert.Null(KautukaDatabaseWriterLease.AcquireForMode(null));
    }

    [Fact]
    public async Task AdmissionFenceBlocksExternalRequestsAndAllowsHealthAndRecoveryProbes()
    {
        bool maintenance = true;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using WebApplication app = builder.Build();
        app.UseAdmissionFence(
            () => maintenance,
            path => KautukaMaintenanceMode.IsAllowedHealthRequest(path)
                || KautukaMaintenanceMode.IsAllowedGrpcRequest(path, "GetHostStatus", "Ping", "GetUpdateStatus", "UpdateDesiredState"));
        app.Run(context => context.Response.WriteAsync("reached", TestContext.Current.CancellationToken));
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage health = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(true);
        using HttpResponseMessage status = await client.PostAsync(new Uri("/Kautuka.Manager/ManagerService/GetHostStatus", UriKind.Relative), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using HttpResponseMessage updateStatus = await client.PostAsync(new Uri("/Kautuka.UpdateService/UpdateService/GetUpdateStatus", UriKind.Relative), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using HttpResponseMessage desiredState = await client.PostAsync(new Uri("/Kautuka.Manager/ManagerService/UpdateDesiredState", UriKind.Relative), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using HttpResponseMessage externalWork = await client.PostAsync(new Uri("/api/channels/send", UriKind.Relative), null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(StatusCodes.Status200OK, (int)health.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (int)status.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (int)updateStatus.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (int)desiredState.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (int)externalWork.StatusCode);

        maintenance = false;
        using HttpResponseMessage resumed = await client.PostAsync(new Uri("/api/channels/send", UriKind.Relative), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(StatusCodes.Status200OK, (int)resumed.StatusCode);
    }

    [Fact]
    public void MaintenanceRemovesApplicationWritersAndKeepsOnlyExplicitHealthWorker()
    {
        var services = new ServiceCollection();
        services.AddHostedService<GatewayOutboxDispatchWorker>();
        services.AddHostedService<ManagerReconcileWorker>();
        services.AddHostedService<ManagerHealthProbeWorker>();

        KautukaMaintenanceMode.SuppressApplicationHostedServices(
            services,
            maintenanceActive: true,
            typeof(ManagerHealthProbeWorker));

        Type[] remaining = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(static descriptor => descriptor.ImplementationType!)
            .ToArray();
        Assert.Equal([typeof(ManagerHealthProbeWorker)], remaining);
    }

    [Fact]
    public void DesiredStateCanBePersistedInMaintenanceWithoutRunningExternalSideEffects()
    {
        Assert.False(KautukaMaintenanceMode.ShouldRunDesiredStateSideEffects(requiresReconciliation: true, maintenanceActive: true));
        Assert.True(KautukaMaintenanceMode.ShouldRunDesiredStateSideEffects(requiresReconciliation: true, maintenanceActive: false));
        Assert.False(KautukaMaintenanceMode.ShouldRunDesiredStateSideEffects(requiresReconciliation: false, maintenanceActive: false));
    }

    private sealed class GatewayOutboxDispatchWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class DispatchCounter { public int Deliveries { get; set; } }

    private sealed class QueuedOutboundWorker(DispatchCounter counter) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { counter.Deliveries++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ManagerReconcileWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class ManagerHealthProbeWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
