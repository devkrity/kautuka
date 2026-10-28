using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;
using Kautuka.UpdateService.State;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kautuka.IntegrationTests;

public sealed class UpdateActivationWorkerTests
{
    [Fact]
    public async Task ExecuteAsyncPersistsPreflightFailureForApplyingRelease()
    {
        UpdateStateRecord initialState = UpdateStateRecord.CreateDefault() with
        {
            State = UpdateWorkflowState.Applying,
            CurrentRelease = CreateRelease("1.0.0", "preview"),
            AvailableRelease = CreateRelease("1.0.1", "preview"),
            StagedRelease = CreateRelease("1.0.1", "preview"),
            PreparedArtifacts = new UpdatePreparedArtifacts(
                TargetReleaseId: "1.0.1",
                PlatformPackagePath: "/tmp/package.tar.gz",
                PlatformExtractedPath: "/tmp/extracted"),
            LastKnownGoodRelease = CreateRelease("1.0.0", "preview"),
        };

        var stateStore = new InMemoryUpdateStateStore(initialState);
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kautuka-update-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var operationStore = new UpdateOperationHistoryStore(Options.Create(new UpdateOptions
            {
                Enabled = true,
                StateRootPath = tempRoot,
            }));

            var worker = new UpdateActivationWorker(
                new ThrowingUpdateServiceIntegration(new InvalidOperationException("Provisioned configuration destination '/etc/kautuka/update-source.json' is not writable.")),
                stateStore,
                operationStore,
                Options.Create(new UpdateOptions { Enabled = true, StateRootPath = tempRoot }),
                NullLogger<UpdateActivationWorker>.Instance);

            await worker.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await WaitForFailurePersistenceAsync(stateStore, operationStore, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await worker.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            UpdateStateRecord updatedState = await stateStore.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(UpdateWorkflowState.Available, updatedState.State);
            Assert.Equal("1.0.1", updatedState.StagedRelease?.Version);
            Assert.Equal("1.0.1", updatedState.PreparedArtifacts?.TargetReleaseId);
            Assert.Equal(
                "Update preflight checks failed: Provisioned configuration destination '/etc/kautuka/update-source.json' is not writable.",
                updatedState.LastError);

            IReadOnlyList<UpdateOperationRecord> history = await operationStore.GetRecentAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            UpdateOperationRecord failure = Assert.Single(history);
            Assert.Equal("apply", failure.OperationType);
            Assert.False(failure.Succeeded);
            Assert.Equal("1.0.0", failure.FromVersion);
            Assert.Equal("1.0.1", failure.ToVersion);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static UpdateReleaseReference CreateRelease(string version, string channel)
        => new(
            ReleaseId: version,
            Version: version,
            Channel: channel,
            ReleaseFingerprint: "fp-" + version,
            PlatformArtifactUrl: null,
            PlatformReleaseAssetUrl: null,
            PlatformReleaseSignatureAssetUrl: null,
            SourceUrl: null,
            PublishedAtUtc: null,
            Tag: version);

    private static async Task WaitForFailurePersistenceAsync(
        InMemoryUpdateStateStore stateStore,
        UpdateOperationHistoryStore operationStore,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            UpdateStateRecord state = await stateStore.GetAsync(cancellationToken).ConfigureAwait(false);
            if (state.State == UpdateWorkflowState.Available)
            {
                IReadOnlyList<UpdateOperationRecord> history = await operationStore.GetRecentAsync(cancellationToken).ConfigureAwait(false);
                if (history.Count > 0)
                {
                    return;
                }
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for the update activation worker to persist the failure state.");
    }

    private sealed class ThrowingUpdateServiceIntegration(Exception exception) : IUpdateServiceIntegration
    {
        public ValueTask<UpdateServiceContext> GetHostContextAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new UpdateServiceContext("/tmp", "/tmp", "1.0.0", "1.0.0", true, 0));

        public ValueTask ActivatePreparedUpdateAsync(UpdatePreparedArtifacts preparedArtifacts, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask ActivateRollbackAsync(UpdateReleaseReference release, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask RequestSupervisorReconcileAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<bool> TryContinuePendingActivationAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<bool>(exception);
    }

    private sealed class InMemoryUpdateStateStore(UpdateStateRecord state) : IUpdateStateStore
    {
        private UpdateStateRecord current = state;

        public ValueTask<UpdateStateRecord> GetAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(current);

        public ValueTask SaveAsync(UpdateStateRecord state, CancellationToken cancellationToken = default)
        {
            current = state;
            return ValueTask.CompletedTask;
        }
    }
}
