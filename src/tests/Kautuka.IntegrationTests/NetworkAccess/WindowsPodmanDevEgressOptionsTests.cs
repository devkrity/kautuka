using System.ComponentModel;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class WindowsPodmanDevEgressOptionsTests
{
    private const string PinnedSandboxImage = "ghcr.io/devkrity/kautuka-base@sha256:675400832f2ced5cf802db66eb067ef5022f65f0551c665d87ae7edc58dc0839";

    [Fact]
    public void HookDirectoryConfigAppendsToExistingAndSubsequentConfigArrays()
    {
        string config = WindowsPodmanDevEgressOptions.RenderHookDirectoryConfig("/home/user/.local/share/kautuka/nac/hooks.d");

        Assert.Contains("hooks_dir = [\"/home/user/.local/share/kautuka/nac/hooks.d\", {append = true}]", config, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/share/containers/oci/hooks.d", config, StringComparison.Ordinal);
        Assert.DoesNotContain("/etc/containers/oci/hooks.d", config, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PodmanMachinePreservesEarlierAndLaterHookDirectoriesWhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Podman Machine hook configuration is a Windows development path.");
        }

        var podman = new PodmanCommandRunner();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PodmanCommandResult machineList;
        try
        {
            machineList = await podman.RunAsync(
                ["machine", "list", "--format", "json"],
                10,
                cancellationToken);
        }
        catch (Win32Exception)
        {
            Assert.Skip("Podman is not installed; the Podman Machine config merge check requires Podman.");
            return;
        }

        if (machineList.ExitCode != 0)
        {
            Assert.Skip("Podman Machine is unavailable; the config merge check requires a running machine.");
            return;
        }

        using JsonDocument machines = JsonDocument.Parse(machineList.StdOut);
        JsonElement machine = machines.RootElement.EnumerateArray()
            .FirstOrDefault(item => item.TryGetProperty("Default", out JsonElement isDefault) && isDefault.GetBoolean());
        if (machine.ValueKind != JsonValueKind.Object
            || !machine.TryGetProperty("Name", out JsonElement machineNameElement)
            || string.IsNullOrWhiteSpace(machineNameElement.GetString()))
        {
            Assert.Skip("Podman has no default machine; the config merge check requires one.");
            return;
        }

        string machineName = machineNameElement.GetString()!;
        PodmanCommandResult version = await podman.RunAsync(
            ["machine", "ssh", machineName, "podman --version"],
            10,
            cancellationToken);
        if (version.ExitCode != 0)
        {
            Assert.Skip("The default Podman Machine is not running.");
            return;
        }

        PodmanCommandResult imageExists = await podman.RunAsync(
            ["machine", "ssh", machineName, $"podman image exists {PinnedSandboxImage}"],
            10,
            cancellationToken);
        if (imageExists.ExitCode != 0)
        {
            Assert.Skip($"Pinned sandbox image '{PinnedSandboxImage}' is not cached in Podman Machine.");
            return;
        }

        string localRoot = Path.Combine(Path.GetTempPath(), "kautuka-podman-hook-merge-" + Guid.NewGuid().ToString("N"));
        string remoteRoot = "/tmp/kautuka-podman-hook-merge-" + Guid.NewGuid().ToString("N");
        string localConfigRoot = Path.Combine(localRoot, "containers");
        Directory.CreateDirectory(Path.Combine(localConfigRoot, "containers.conf.d"));
        try
        {
            await WriteLocalFileAsync(
                Path.Combine(localConfigRoot, "containers.conf"),
                $"[engine]{Environment.NewLine}hooks_dir = [\"{remoteRoot}/hooks-user\"]{Environment.NewLine}");
            await WriteLocalFileAsync(
                Path.Combine(localConfigRoot, "containers.conf.d", "50-kautuka.conf"),
                WindowsPodmanDevEgressOptions.RenderHookDirectoryConfig($"{remoteRoot}/hooks-nac"));
            await WriteLocalFileAsync(
                Path.Combine(localConfigRoot, "containers.conf.d", "99-user.conf"),
                $"[engine]{Environment.NewLine}hooks_dir = [\"{remoteRoot}/hooks-later\"]{Environment.NewLine}");

            foreach ((string directory, string marker) in new[]
            {
                ("hooks-user", "user"),
                ("hooks-nac", "nac"),
                ("hooks-later", "later"),
            })
            {
                string hook = JsonSerializer.Serialize(new
                {
                    version = "1.0.0",
                    hook = new
                    {
                        path = "/usr/bin/touch",
                        args = new[] { "touch", $"{remoteRoot}/{marker}-ran" },
                    },
                    when = new { always = true },
                    stages = new[] { "createRuntime" },
                });
                await WriteLocalFileAsync(Path.Combine(localRoot, directory + ".json"), hook);
            }

            PodmanCommandResult makeDirectories = await podman.RunAsync(
                ["machine", "ssh", machineName, $"mkdir -p '{remoteRoot}/containers/containers.conf.d' '{remoteRoot}/hooks-user' '{remoteRoot}/hooks-nac' '{remoteRoot}/hooks-later'"],
                10,
                cancellationToken);
            Assert.Equal(0, makeDirectories.ExitCode);

            await CopyToMachineAsync(podman, machineName, Path.Combine(localConfigRoot, "containers.conf"), $"{remoteRoot}/containers/containers.conf", cancellationToken);
            await CopyToMachineAsync(
                podman,
                machineName,
                Path.Combine(localConfigRoot, "containers.conf.d", "50-kautuka.conf"),
                $"{remoteRoot}/containers/containers.conf.d/50-kautuka.conf",
                cancellationToken);
            await CopyToMachineAsync(
                podman,
                machineName,
                Path.Combine(localConfigRoot, "containers.conf.d", "99-user.conf"),
                $"{remoteRoot}/containers/containers.conf.d/99-user.conf",
                cancellationToken);
            await CopyToMachineAsync(podman, machineName, Path.Combine(localRoot, "hooks-user.json"), $"{remoteRoot}/hooks-user/user.json", cancellationToken);
            await CopyToMachineAsync(podman, machineName, Path.Combine(localRoot, "hooks-nac.json"), $"{remoteRoot}/hooks-nac/nac.json", cancellationToken);
            await CopyToMachineAsync(podman, machineName, Path.Combine(localRoot, "hooks-later.json"), $"{remoteRoot}/hooks-later/later.json", cancellationToken);

            PodmanCommandResult run = await podman.RunAsync(
                ["machine", "ssh", machineName,
                    $"XDG_CONFIG_HOME='{remoteRoot}' podman run --pull=never --rm --entrypoint /bin/true {PinnedSandboxImage}"],
                30,
                cancellationToken);
            Assert.True(run.ExitCode == 0, $"Podman {version.StdOut.Trim()} failed the hook config run: {run.StdErr}");

            PodmanCommandResult markers = await podman.RunAsync(
                ["machine", "ssh", machineName,
                    $"test -f '{remoteRoot}/user-ran' && test -f '{remoteRoot}/nac-ran' && test -f '{remoteRoot}/later-ran'"],
                10,
                cancellationToken);
            Assert.True(
                markers.ExitCode == 0,
                $"Podman {version.StdOut.Trim()} did not execute hooks from the existing, NAC, and later hook directories. {markers.StdErr}");

            string replacementConfig = $"[engine]{Environment.NewLine}hooks_dir = [\"{remoteRoot}/hooks-nac\"]{Environment.NewLine}";
            await File.WriteAllTextAsync(
                Path.Combine(localConfigRoot, "containers.conf.d", "50-kautuka.conf"),
                replacementConfig,
                cancellationToken);
            await CopyToMachineAsync(
                podman,
                machineName,
                Path.Combine(localConfigRoot, "containers.conf.d", "50-kautuka.conf"),
                $"{remoteRoot}/containers/containers.conf.d/50-kautuka.conf",
                cancellationToken);
            PodmanCommandResult clearMarkers = await podman.RunAsync(
                ["machine", "ssh", machineName, $"rm -f '{remoteRoot}/user-ran' '{remoteRoot}/nac-ran' '{remoteRoot}/later-ran'"],
                10,
                cancellationToken);
            Assert.Equal(0, clearMarkers.ExitCode);
            // Append mode intentionally affects later fragments too. The negative control
            // proves normal replacement semantics when the NAC fragment does not append.
            PodmanCommandResult negativeControl = await podman.RunAsync(
                ["machine", "ssh", machineName,
                    $"XDG_CONFIG_HOME='{remoteRoot}' podman run --pull=never --rm --entrypoint /bin/true {PinnedSandboxImage}"],
                30,
                cancellationToken);
            Assert.True(
                negativeControl.ExitCode == 0,
                $"Podman {version.StdOut.Trim()} failed the replacement negative control: {negativeControl.StdErr}");
            PodmanCommandResult replacementMarkers = await podman.RunAsync(
                ["machine", "ssh", machineName,
                    $"test ! -e '{remoteRoot}/user-ran' && test ! -e '{remoteRoot}/nac-ran' && test -e '{remoteRoot}/later-ran'"],
                10,
                cancellationToken);
            Assert.True(
                replacementMarkers.ExitCode == 0,
                $"Podman {version.StdOut.Trim()} did not apply later replacement semantics in the negative control. {replacementMarkers.StdErr}");
        }
        finally
        {
            try
            {
                _ = await podman.RunAsync(
                    ["machine", "ssh", machineName, $"rm -rf '{remoteRoot}'"],
                    10,
                    CancellationToken.None);
            }
            catch
            {
                // Cleanup is best-effort and must not mask a test failure.
            }
            finally
            {
                try
                {
                    if (Directory.Exists(localRoot))
                    {
                        Directory.Delete(localRoot, recursive: true);
                    }
                }
                catch
                {
                    // Local cleanup must also leave the original test result intact.
                }
            }
        }
    }

    private static async Task WriteLocalFileAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
    }

    private static async Task CopyToMachineAsync(
        PodmanCommandRunner podman,
        string machineName,
        string source,
        string remoteDestination,
        CancellationToken cancellationToken)
    {
        PodmanCommandResult copy = await podman.RunAsync(
            ["machine", "cp", source, $"{machineName}:{remoteDestination}"],
            20,
            cancellationToken);
        Assert.True(copy.ExitCode == 0, $"Failed to stage hook config into Podman Machine: {copy.StdErr}");
    }
}
