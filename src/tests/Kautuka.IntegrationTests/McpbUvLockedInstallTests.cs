using System.Diagnostics;

using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class McpbUvLockedInstallTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockedProjectUsesHashedProxySyncBeforeInstallingProject(bool sandboxArtifact)
    {
        if (OperatingSystem.IsWindows()) return;
        string[] calls = await RunInstallScriptAsync(sandboxArtifact, lockFile: true);
        int export = Array.FindIndex(calls, line => line.StartsWith("export ", StringComparison.Ordinal));
        int sync = Array.FindIndex(calls, line => line.StartsWith("pip sync ", StringComparison.Ordinal));
        int project = Array.FindIndex(calls, line => line.StartsWith("pip install ", StringComparison.Ordinal));
        Assert.True(export >= 0 && sync > export && project > sync);
        Assert.Contains("--frozen --no-dev --no-emit-project", calls[export], StringComparison.Ordinal);
        Assert.Contains("--require-hashes", calls[sync], StringComparison.Ordinal);
        Assert.Contains("--index-url http://proxy.invalid/pypi/simple/", calls[sync], StringComparison.Ordinal);
        Assert.Contains("--no-deps --index-url http://proxy.invalid/pypi/simple/ .", calls[project], StringComparison.Ordinal);
        Assert.DoesNotContain(calls, line => line.StartsWith("sync ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectWithoutLockKeepsExistingUvSync(bool sandboxArtifact)
    {
        if (OperatingSystem.IsWindows()) return;
        string[] calls = await RunInstallScriptAsync(sandboxArtifact, lockFile: false);
        string sync = Assert.Single(calls, line => line.StartsWith("sync ", StringComparison.Ordinal));
        Assert.Contains("--no-dev", sync, StringComparison.Ordinal);
        Assert.Contains("--index-url http://proxy.invalid/pypi/simple/", sync, StringComparison.Ordinal);
        Assert.Equal(sandboxArtifact, sync.Contains("--no-editable", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, line => line.StartsWith("export ", StringComparison.Ordinal) || line.StartsWith("pip sync ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LockedVirtualProjectDoesNotForceProjectInstallation()
    {
        if (OperatingSystem.IsWindows()) return;
        string[] calls = await RunInstallScriptAsync(sandboxArtifact: true, lockFile: true, package: false);
        Assert.Contains(calls, line => line.StartsWith("pip sync ", StringComparison.Ordinal));
        Assert.Contains(calls, line => line.Contains("--only-emit-project", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, line => line.StartsWith("pip install ", StringComparison.Ordinal));
    }

    private static async Task<string[]> RunInstallScriptAsync(bool sandboxArtifact, bool lockFile, bool package = true)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string root = Path.Combine(Path.GetTempPath(), "kautuka-uv-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string uv = Path.Combine(root, "uv");
            await File.WriteAllTextAsync(uv, "#!/bin/sh\nif [ \"$1\" = --version ]; then echo 'uv 0.11.19'; exit; fi\nprintf '%s\\n' \"$*\" >> \"$CALLS\"\nif [ \"$1\" = venv ]; then mkdir -p .venv/bin; touch .venv/bin/python; chmod +x .venv/bin/python; fi\ncase \" $* \" in *' --only-emit-project '*) if [ \"$PACKAGE\" = 1 ]; then printf '.\\n' > \"$TMPDIR/kautuka-uv-project.txt\"; else : > \"$TMPDIR/kautuka-uv-project.txt\"; fi ;; esac\n", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(uv, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(root, "pyproject.toml"), "[project]\n", TestContext.Current.CancellationToken);
            if (lockFile) await File.WriteAllTextAsync(Path.Combine(root, "uv.lock"), "lock", TestContext.Current.CancellationToken);
            string calls = Path.Combine(root, "calls.txt");
            var start = new ProcessStartInfo("/bin/sh") { WorkingDirectory = root, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(McpbImageMaterializer.BuildLanguagePackageInstallScript("uv", sandboxArtifact));
            start.Environment["PATH"] = root + ":" + Environment.GetEnvironmentVariable("PATH");
            start.Environment["PIP_INDEX_URL"] = "http://proxy.invalid/pypi/simple/";
            start.Environment["UV_INDEX_URL"] = "http://proxy.invalid/pypi/simple/";
            start.Environment["CALLS"] = calls;
            start.Environment["TMPDIR"] = root;
            start.Environment["PACKAGE"] = package ? "1" : "0";
            start.Environment.Remove("UV_PROJECT_ENVIRONMENT");
            using Process process = Process.Start(start)!;
            string error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, error);
            return await File.ReadAllLinesAsync(calls, TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(root, true); }
    }
}
