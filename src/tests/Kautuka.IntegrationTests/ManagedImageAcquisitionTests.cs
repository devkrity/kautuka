using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ManagedImageAcquisitionTests
{
    private static readonly string source = "quay.io/example/connector@sha256:" + new string('a', 64);
    private static readonly string config = "sha256:" + new string('b', 64);

    [Fact]
    public async Task ReadyForegroundImageDoesNotWaitForAnUnrelatedBackgroundPreparation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string readySource = source.Replace("connector@", "ready@", StringComparison.Ordinal);
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64";
        var runner = new PodmanCommandRunner(async (args, token) =>
        {
            if (args[1] == "exists" && args[^1] == source)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return new PodmanCommandResult(1, "", "");
            }
            Assert.Equal(readySource, args[^1]);
            return new PodmanCommandResult(0, args[1] == "inspect"
                ? "[{\"Id\":\"" + config[7..] + "\",\"Os\":\"linux\",\"Architecture\":\"" + architecture + "\"}]" : "", "");
        });
        using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, Path.GetTempPath());
        Task<ResolvedRuntimeImage> pending = runtime.ResolveRuntimeImageAsync(source, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            ResolvedRuntimeImage ready = await runtime.ResolveCachedRuntimeImageAsync(readySource, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(config, ready.LocalImageId);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundCacheMissCannotLoadOrAcquireEvenWithAnApprovedRegistry(bool signedArchive)
    {
        var calls = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            return Task.FromResult(new PodmanCommandResult(1, "", ""));
        });
        var options = new ContainerEgressOptions { Enabled = true, Grants = [new() { ScopeType = "core", Role = "materializer", ComponentId = "image-acquisition", Destination = "quay.io", Port = 443 }] };
        RuntimeImageAllowlist inventory = signedArchive
            ? new(1, [new("fixture", source, "missing.oci", new string('0', 64), "linux/amd64", config)])
            : RuntimeImageAllowlist.Empty;
        using var runtime = new ContainerEgressRuntime(runner, options, inventory, Path.GetTempPath());
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResolveCachedRuntimeImageAsync(source, TestContext.Current.CancellationToken));
        Assert.Contains("background preparation", error.Message, StringComparison.Ordinal);
        Assert.Single(calls);
        Assert.Equal(new[] { "image", "exists", signedArchive ? config : source }, calls[0]);
    }

    [Fact]
    public async Task UnknownRegistryIsRejectedBeforeBootstrapOrHelperCreation()
    {
        var calls = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            return Task.FromResult(new PodmanCommandResult(1, "", ""));
        });
        using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, Path.GetTempPath());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResolveRuntimeImageAsync(source, TestContext.Current.CancellationToken));
        Assert.Single(calls);
        Assert.Equal(new[] { "image", "exists", source }, calls[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePinnedCacheHitPreservesAuthorizationSourceAndChecksPlatform(bool wrongPlatform)
    {
        var calls = new List<string[]>();
        string platform = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64";
        var runner = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            return Task.FromResult(new PodmanCommandResult(0, args[1] == "inspect"
                ? "[{\"Id\":\"" + config[7..] + "\",\"Os\":\"linux\",\"Architecture\":\"" + (wrongPlatform ? "unsupported" : platform) + "\"}]" : "", ""));
        });
        using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, Path.GetTempPath());
        if (wrongPlatform) await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResolveRuntimeImageAsync(source, TestContext.Current.CancellationToken));
        else
        {
            ResolvedRuntimeImage resolved = await runtime.ResolveRuntimeImageAsync(source, TestContext.Current.CancellationToken);
            Assert.Equal(source, resolved.SourceReference);
            Assert.Equal(config, resolved.LocalImageId);
        }
        Assert.Equal(2, calls.Count);
        Assert.All(calls, args => Assert.Equal("image", args[0]));
    }

    [Fact]
    public async Task TimedOutCacheQueryCannotBecomeARegistryRequest()
    {
        int calls = 0;
        var runner = new PodmanCommandRunner((_, _) =>
        {
            calls++;
            return Task.FromResult(new PodmanCommandResult(124, "", "timeout", ProcessTerminationReason.TimedOut));
        });
        using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, Path.GetTempPath());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResolveRuntimeImageAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }
}
