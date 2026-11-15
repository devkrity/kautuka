using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class McpbImageMaterializerConcurrencyTests
{
    [Fact]
    public async Task ConcurrentRequestsReuseImageBuiltWhileWaiting()
    {
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int imageChecks = 0;
        int builds = 0;
        int imageReady = 0;
        var runner = new PodmanCommandRunner(async (args, cancellationToken) =>
        {
            if (args[0] == "image" && args[1] == "exists")
            {
                if (Interlocked.Increment(ref imageChecks) == 3)
                {
                    secondCheck.TrySetResult();
                }

                return new PodmanCommandResult(Volatile.Read(ref imageReady) == 0 ? 1 : 0, "", "");
            }

            if (args[0] == "build")
            {
                Interlocked.Increment(ref builds);
                buildStarted.TrySetResult();
                await finishBuild.Task.WaitAsync(cancellationToken);
            }
            else if (args[0] == "tag")
            {
                Volatile.Write(ref imageReady, 1);
            }

            return new PodmanCommandResult(0, "", "", ProcessTerminationReason.Completed);
        });
        var materializer = new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance);
        var connector = new WorkspaceMcpContainerConnectorDefinition(
            "test", "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
            "test", [], true, true, null, "binary", [], 30, 600);
        CancellationToken token = TestContext.Current.CancellationToken;

        Task<string> first = materializer.EnsureImageAsync(connector, isBraked: false, cancellationToken: token);
        await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        Task<string> second = materializer.EnsureImageAsync(connector, isBraked: false, cancellationToken: token);
        await secondCheck.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        finishBuild.TrySetResult();

        string[] images = await Task.WhenAll(first, second);
        Assert.Equal(images[0], images[1]);
        Assert.Equal(1, builds);
    }
    [Fact]
    public async Task CanceledProbeFinishesBuildAndRetryReusesImage()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int imageChecks = 0;
        int builds = 0;
        int ready = 0;
        var runner = new PodmanCommandRunner(async (args, token) =>
        {
            if (args[0] == "image" && args[1] == "exists")
            {
                if (Interlocked.Increment(ref imageChecks) == 3) retryChecked.TrySetResult();
                return new PodmanCommandResult(Volatile.Read(ref ready) == 0 ? 1 : 0, "", "");
            }
            if (args[0] == "build")
            {
                Interlocked.Increment(ref builds);
                started.TrySetResult();
                await finish.Task.WaitAsync(token);
            }
            else if (args[0] == "tag") Volatile.Write(ref ready, 1);
            return new PodmanCommandResult(0, "", "", ProcessTerminationReason.Completed);
        });
        var materializer = new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance);
        var connector = new WorkspaceMcpContainerConnectorDefinition(
            "test", "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
            "test", [], true, true, null, "binary", [], 30, 600);
        using var request = new CancellationTokenSource();
        CancellationToken testToken = TestContext.Current.CancellationToken;
        Task<string> first = WorkspaceContainerConnectorExecutionService.CompleteProbeMaterializationAsync(
            token => materializer.EnsureImageAsync(connector, false, token),
            request.Token, testToken, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
        await request.CancelAsync();
        Task<string> retry = WorkspaceContainerConnectorExecutionService.CompleteProbeMaterializationAsync(
            token => materializer.EnsureImageAsync(connector, false, token),
            testToken, testToken, CancellationToken.None);
        await retryChecked.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
        finish.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.StartsWith("localhost/", await retry, StringComparison.Ordinal);
        Assert.Equal(1, builds);
    }

}
