using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceContainerSupervisorReadinessTests
{
    [Fact]
    public async Task SandboxImagePreparationDoesNotCloseTheRuntimeGate()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "kautuka-readiness-" + Guid.NewGuid().ToString("N"));
        var store = new WorkspaceDesiredStateStore();
        ManagerRuntimeSettings runtimeSettings = CreateRuntimeSettings(root);
        store.ApplyDesiredState(
            1,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            runtimeSettings,
            [],
            [],
            []);
        var gate = new WorkspaceReadinessGate();
        var adapter = new BlockingImageRuntimeAdapter();
        WorkspaceContainerSupervisorWorker worker = CreateWorker(store, gate, adapter, root);

        try
        {
            await worker.StartAsync(cancellationToken).ConfigureAwait(true);
            await adapter.FirstImageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);

            Assert.True(gate.IsReady);

            store.ApplyDesiredState(
                2,
                Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
                CreateRuntimeSettings(root, defaultTimeout: 121),
                [],
                [],
                []);
            adapter.ReleaseFirstImage();
            await adapter.SecondImageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);

            Assert.True(gate.IsReady);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(true);
            worker.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RuntimeUnavailableDoesNotSignalTheGate()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "kautuka-readiness-" + Guid.NewGuid().ToString("N"));
        var store = new WorkspaceDesiredStateStore();
        store.ApplyDesiredState(
            1,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            CreateRuntimeSettings(root),
            [],
            [],
            []);
        var gate = new WorkspaceReadinessGate();
        gate.SignalReady();
        var adapter = new UnavailableRuntimeAdapter();
        WorkspaceContainerSupervisorWorker worker = CreateWorker(store, gate, adapter, root);

        try
        {
            await worker.StartAsync(cancellationToken).ConfigureAwait(true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            while (gate.IsReady)
            {
                await Task.Delay(10, timeout.Token).ConfigureAwait(true);
            }

            Assert.False(gate.IsReady);
            Assert.Equal(0, adapter.ImagePreparations);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(true);
            worker.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static WorkspaceContainerSupervisorWorker CreateWorker(
        WorkspaceDesiredStateStore store,
        WorkspaceReadinessGate gate,
        IManagerRuntimeAdapter adapter,
        string root)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = root,
                ["Kautuka:Mode"] = "dev",
            })
            .Build();
        var paths = new WorkspaceRuntimePaths(
            configuration,
            new TestHostEnvironment(),
            NullLogger<WorkspaceRuntimePaths>.Instance);
        var startup = Options.Create(new ManagerContainerStartupOptions());
        var sidecarSupervisor = new WorkspaceSidecarSupervisor(
            WorkspaceSidecarCatalog.Empty,
            new UnusedHttpClientFactory(),
            new PodmanCommandRunner(),
            startup,
            NullLogger<WorkspaceSidecarSupervisor>.Instance);
        var connectorExecution = new WorkspaceContainerConnectorExecutionService(
            store,
            new PodmanCommandRunner(),
            new McpbImageMaterializer(new PodmanCommandRunner(), NullLogger<McpbImageMaterializer>.Instance),
            new UnusedContainerTool(),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);

        return new WorkspaceContainerSupervisorWorker(
            store,
            adapter,
            sidecarSupervisor,
            connectorExecution,
            paths,
            gate,
            new WorkspaceExecTracker(),
            startup,
            NullLogger<WorkspaceContainerSupervisorWorker>.Instance);
    }

    private static ManagerRuntimeSettings CreateRuntimeSettings(string root, int defaultTimeout = 120)
        => new(
            new ManagerWorkspaceDefinition { BaseRoot = root },
            new ManagerSandboxRuntimeDefinition
            {
                Image = "kautuka-base:latest",
                DefaultTimeout = defaultTimeout,
            },
            new WorkspaceTelemetryDefinition());

    private sealed class BlockingImageRuntimeAdapter : IManagerRuntimeAdapter
    {
        private readonly TaskCompletionSource firstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int imagePreparations;

        public TaskCompletionSource FirstImageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondImageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstImage() => firstRelease.TrySetResult();

        public void ApplyRuntimeSettings(ManagerRuntimeSettings runtimeSettings)
        {
        }

        public Task<bool> EnsureReadyAsync(ManagerRuntimeSettings runtimeSettings, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public async Task<string?> PrepareSandboxImageAsync(ManagerSandboxRuntimeDefinition sandbox, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref imagePreparations);
            if (call == 1)
            {
                FirstImageEntered.TrySetResult();
                await firstRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            SecondImageEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return null;
        }

        public string ResolveSandboxImage(string configuredImage) => configuredImage;

        public Task<string[]> GetManagedContainerNamesAsync(IReadOnlyCollection<string> trackedContainers, CancellationToken cancellationToken)
            => Task.FromResult(Array.Empty<string>());

        public Task StopContainersAsync(IReadOnlyCollection<string> containerNames, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<ManagedContainerReconcileResult> RemoveOrphanedManagedContainersAsync(
            string role,
            IReadOnlyCollection<string> desiredContainerNames,
            CancellationToken cancellationToken)
            => Task.FromResult(new ManagedContainerReconcileResult(true, 0, null));

        public Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class UnavailableRuntimeAdapter : IManagerRuntimeAdapter
    {
        public int ImagePreparations { get; private set; }

        public void ApplyRuntimeSettings(ManagerRuntimeSettings runtimeSettings)
        {
        }

        public Task<bool> EnsureReadyAsync(ManagerRuntimeSettings runtimeSettings, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<string?> PrepareSandboxImageAsync(ManagerSandboxRuntimeDefinition sandbox, CancellationToken cancellationToken)
        {
            ImagePreparations++;
            return Task.FromResult<string?>(null);
        }

        public string ResolveSandboxImage(string configuredImage) => configuredImage;

        public Task<string[]> GetManagedContainerNamesAsync(IReadOnlyCollection<string> trackedContainers, CancellationToken cancellationToken)
            => Task.FromResult(Array.Empty<string>());

        public Task StopContainersAsync(IReadOnlyCollection<string> containerNames, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<ManagedContainerReconcileResult> RemoveOrphanedManagedContainersAsync(
            string role,
            IReadOnlyCollection<string> desiredContainerNames,
            CancellationToken cancellationToken)
            => Task.FromResult(new ManagedContainerReconcileResult(true, 0, null));

        public Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class UnusedContainerTool : IContainerTool
    {
        public Task PrimeWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ContainerContext> RebuildWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OciSandboxSession> SpawnSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ContainerExecutionResult> RunShellAsync(
            ContainerContext context,
            string command,
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OciSandboxSession> InspectSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CloseSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException(name);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Kautuka.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
