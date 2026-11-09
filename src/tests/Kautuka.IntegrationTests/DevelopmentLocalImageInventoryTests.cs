using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class DevelopmentLocalImageInventoryTests
{
    private static readonly string LocalId = "sha256:" + new string('a', 64);
    private const string Source = "localhost/kautuka-dev-gateway:test";

    [Fact]
    public void GateRequiresRootOwnedUnwritableMarkerAndOptIn()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.UserName == "root", "Requires a disposable Linux root-owned marker.");
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "kautuka-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, "marker");
        try
        {
            File.WriteAllText(marker, "kautuka-dev-local-images-v1\n");
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Assert.False(DevelopmentLocalImageGate.Enabled(null, marker));
            Assert.True(DevelopmentLocalImageGate.Enabled("1", marker));
            File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
            Assert.False(DevelopmentLocalImageGate.Enabled("1", marker));
            File.Delete(marker);
            File.CreateSymbolicLink(marker, "/etc/passwd");
            Assert.False(DevelopmentLocalImageGate.Enabled("1", marker));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LocalInventoryRequiresExplicitPermissionAndCompleteBootstrapArchives()
    {
        string core = Entry("core-gateway", Source, "null", "null", "linux/amd64", LocalId);
        string bootstrap = Entry("envoy", "localhost/envoy:test", "\"envoy.oci\"", "\"" + new string('b', 64) + "\"", "linux/amd64", LocalId);
        WithInventory(true, core + "," + bootstrap, path =>
        {
            Assert.Throws<InvalidOperationException>(() => RuntimeImageAllowlistLoader.Load(path, true));
            Assert.Equal(2, RuntimeImageAllowlistLoader.Load(path, true, allowDevelopmentLocalImages: true).Images.Count);
        });
        WithInventory(false, core, path => Assert.Throws<InvalidOperationException>(() => RuntimeImageAllowlistLoader.Load(path, true, allowDevelopmentLocalImages: true)));
        WithInventory(true, Entry("envoy", Source, "null", "null", "linux/amd64", LocalId), path => Assert.Throws<InvalidOperationException>(() => RuntimeImageAllowlistLoader.Load(path, true, allowDevelopmentLocalImages: true)));
        WithInventory(true, core + "," + "{\"id\":\"envoy\",\"image\":\"localhost/envoy:test\",\"archive\":null}", path => Assert.Throws<InvalidOperationException>(() => RuntimeImageAllowlistLoader.Load(path, true, allowDevelopmentLocalImages: true)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LocalCoreResolvesOnlyItsImmutableIdAndNeverLoadsOrPulls(bool exists, bool mismatch)
    {
        var entry = new RuntimeImageEntry("core-gateway", Source, null, null, "linux/amd64", LocalId);
        var calls = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            string inspectedId = mismatch ? "sha256:" + new string('c', 64) : LocalId;
            string output = args[1] == "inspect" ? "[{\"Id\":\"" + inspectedId + "\",\"Os\":\"linux\",\"Architecture\":\"amd64\"}]" : "";
            return Task.FromResult(new PodmanCommandResult(args[1] == "exists" && !exists ? 1 : 0, output, ""));
        });
        var store = new PodmanImageArchiveStore(runner, Path.GetTempPath(), new RuntimeImageAllowlist(1, [entry], true), NullLogger<PodmanImageArchiveStore>.Instance);
        if (!exists || mismatch)
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResolveCachedAsync(Source, TestContext.Current.CancellationToken));
        else
            Assert.Equal(LocalId, (await store.ResolveCachedAsync(Source, TestContext.Current.CancellationToken)).LocalImageId);
        Assert.All(calls, args => { Assert.Equal("image", args[0]); Assert.Equal(LocalId, args[^1]); });
        Assert.Equal(exists ? 2 : 1, calls.Count);
    }

    private static string Entry(string id, string image, string archive, string hash, string platform, string localId)
        => "{\"id\":\"" + id + "\",\"image\":\"" + image + "\",\"archive\":" + archive + ",\"archiveSha256\":" + hash + ",\"platform\":\"" + platform + "\",\"localImageId\":\"" + localId + "\"}";

    private static void WithInventory(bool development, string entries, Action<string> action)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1,\"developmentLocalImages\":" + (development ? "true" : "false") + ",\"images\":[" + entries + "]}");
            action(path);
        }
        finally { File.Delete(path); }
    }
}
