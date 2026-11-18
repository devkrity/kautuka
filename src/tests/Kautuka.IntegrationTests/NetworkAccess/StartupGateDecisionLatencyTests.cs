using System.Diagnostics;
using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class StartupGateDecisionLatencyTests
{
    [Fact]
    public async Task ClosingGate_DoesNotHoldDecisionLockWhileStatusWriteIsBlocked()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("A FIFO is used to deterministically block the status-file write.");
        }

        string root = Path.Combine(Path.GetTempPath(), $"nac-gate-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var podman = new PodmanCommandRunner();
        await using var manager = new EgressTopologyManager(
            podman,
            new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
            new WorkloadPostStartVerifier(podman),
            new WorkloadRegistry(),
            root);

        string statusPath = Path.Combine(root, "nac", "startup-gate.status");
        Task? closeTask = null;
        Task<bool>? decisionTask = null;
        Task<string>? readerTask = null;
        try
        {
            SetGate(manager, isOpen: true);
            File.Delete(statusPath);
            CreateFifo(statusPath);

            using var setterStarted = new ManualResetEventSlim();
            closeTask = Task.Run(() =>
            {
                setterStarted.Set();
                manager.CloseLaunchAdmission();
            }, cancellationToken);
            Assert.True(setterStarted.Wait(TimeSpan.FromSeconds(2), cancellationToken));

            // Observe the state mutation directly before asking through the public locked getter.
            // The getter itself is the behavior under test and would block in the old close path.
            FieldInfo gateState = typeof(EgressTopologyManager).GetField("isStartupGateOpen", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Expected the in-memory startup gate state field.");
            bool stateClosed = SpinWait.SpinUntil(() =>
            {
                Thread.MemoryBarrier();
                return !(bool)gateState.GetValue(manager)!;
            }, TimeSpan.FromSeconds(2));
            Assert.True(stateClosed, "CloseLaunchAdmission did not switch off the in-memory gate.");

            decisionTask = Task.Run(() => manager.IsStartupGateOpen, cancellationToken);
            Task completed = await Task.WhenAny(decisionTask, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            Assert.Same(decisionTask, completed);
            Assert.False(await decisionTask);

            // Negative control: the gate is actually switched off while persistence is stalled.
            Assert.False(closeTask.IsCompleted);

            readerTask = Task.Run(() => File.ReadAllText(statusPath));
            Assert.Equal("closed", await readerTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken));
            await closeTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        finally
        {
            // Cleanup is independent of test cancellation. One reader releases the blocked write,
            // including after an assertion timeout in the fail-first version.
            if (closeTask is { IsCompleted: false } && readerTask is null && File.Exists(statusPath))
            {
                readerTask = Task.Run(() => File.ReadAllText(statusPath));
            }
            if (readerTask is not null) await WaitForCleanupAsync(readerTask);
            if (closeTask is not null) await WaitForCleanupAsync(closeTask);
            if (decisionTask is not null) await WaitForCleanupAsync(decisionTask);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void SetGate(EgressTopologyManager manager, bool isOpen) =>
        typeof(EgressTopologyManager).GetProperty(nameof(EgressTopologyManager.IsStartupGateOpen))!
            .SetValue(manager, isOpen);

    private static void CreateFifo(string path)
    {
        var startInfo = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mkfifo.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"mkfifo exited with code {process.ExitCode}.");
        }
    }

    private static async Task WaitForCleanupAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch { }
    }
}
