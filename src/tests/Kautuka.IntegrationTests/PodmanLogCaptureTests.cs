using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class PodmanLogCaptureTests
{
    [Fact]
    public async Task BothLogStreamsShareOneCaptureLimitAndReportTruncation()
    {
        string[]? invoked = null;
        var runner = new PodmanCommandRunner((arguments, _) =>
        {
            invoked = [.. arguments];
            return Task.FromResult(new PodmanCommandResult(0, new string('a', 800), new string('b', 800)));
        });
        PodmanLogBatch batch = await runner.ReadLogsAsync("exact-container-id", "2026-11-07T00:00:00Z", "2026-11-07T00:00:03Z", 1000, TestContext.Current.CancellationToken);
        Assert.True(batch.Truncated);
        Assert.Equal(1000, batch.Result.StdOut.Length + batch.Result.StdErr.Length);
        Assert.Equal(new[] { "logs", "--timestamps", "--since", "2026-11-07T00:00:00Z", "--until", "2026-11-07T00:00:03Z", "--tail", "512", "exact-container-id" }, invoked);
    }

    [Fact]
    public async Task CaptureRetainsNativeFailureAndCompleteSmallOutput()
    {
        var runner = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(125, "", "container disappeared")));
        PodmanLogBatch batch = await runner.ReadLogsAsync("exact-container-id", "0", "1", 1000, TestContext.Current.CancellationToken);
        Assert.False(batch.Truncated);
        Assert.Equal(125, batch.Result.ExitCode);
        Assert.Equal("container disappeared", batch.Result.StdErr);
    }

    [Fact]
    public async Task FullTailReportsPossibleLossEvenWhenCharacterBudgetIsNotReached()
    {
        var runner = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(0, "", string.Concat(Enumerable.Repeat("event\n", 512)))));
        PodmanLogBatch batch = await runner.ReadLogsAsync("exact-container-id", "0", "1", 10000, TestContext.Current.CancellationToken);
        Assert.False(batch.Truncated);
        Assert.True(batch.TailSaturated);
    }
}
