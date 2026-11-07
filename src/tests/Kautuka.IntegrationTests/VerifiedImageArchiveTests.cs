using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class VerifiedImageArchiveTests
{
    private static readonly string source = "ghcr.io/devkrity/archive-fixture@sha256:" + new string('a', 64);
    private static readonly string localId = "sha256:" + new string('b', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectOnlyCannotLoadWhenAnImageIsMissingOrDisappears(bool disappears)
    {
        using var fixture = new ArchiveFixture();
        var calls = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            return Task.FromResult(new PodmanCommandResult(disappears && args[1] == "exists" ? 0 : 1, "", ""));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store(runner, fixture.Entry)
            .ResolveCachedAsync(source, TestContext.Current.CancellationToken, allowLoad: false));
        Assert.DoesNotContain(calls, args => args[0] == "load");
        Assert.Equal(disappears ? 2 : 1, calls.Count);
    }

    [Fact]
    public async Task ConvertedArchiveResolvesTheSignedConfigWithoutRepositoryMetadata()
    {
        using var fixture = new ArchiveFixture();
        RuntimeImageEntry entry = fixture.Entry;
        var calls = new List<string[]>();
        bool loaded = false;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            if (args[0] == "load") { loaded = true; return Task.FromResult(new PodmanCommandResult(0, "loaded", "")); }
            Assert.Equal(localId, args[^1]);
            return Task.FromResult(args[1] == "exists" ? new PodmanCommandResult(loaded ? 0 : 1, "", "")
                : new PodmanCommandResult(0, Inspection(localId[7..], "amd64"), ""));
        });
        ResolvedRuntimeImage resolved = await fixture.Store(runner, entry).ResolveCachedAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(new ResolvedRuntimeImage(source, "linux/amd64", localId), resolved);
        Assert.Single(calls, args => args[0] == "load" && args[2] == fixture.Archive);
        Assert.DoesNotContain(calls, args => args[0] == "pull" || args.Contains(source, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptArchiveIsRejectedBeforeNativeLoad(bool legacyEntryPoint)
    {
        using var fixture = new ArchiveFixture();
        var calls = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) => { calls.Add([.. args]); return Task.FromResult(new PodmanCommandResult(1, "", "")); });
        RuntimeImageEntry entry = fixture.Entry with { ArchiveSha256 = new string('0', 64) };
        PodmanImageArchiveStore store = fixture.Store(runner, entry);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (legacyEntryPoint) await store.TryEnsureImageAsync(source, TestContext.Current.CancellationToken);
            else await store.ResolveCachedAsync(source, TestContext.Current.CancellationToken);
        });
        Assert.Single(calls);
        Assert.Equal(new[] { "image", "exists", localId }, calls[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConfigOrPlatformMismatchCannotBeAdopted(bool wrongPlatform)
    {
        using var fixture = new ArchiveFixture();
        var runner = new PodmanCommandRunner((args, _) => Task.FromResult(new PodmanCommandResult(0,
            args[1] == "inspect" ? Inspection(wrongPlatform ? localId : "sha256:" + new string('c', 64), wrongPlatform ? "arm64" : "amd64") : "", "")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store(runner, fixture.Entry).ResolveCachedAsync(source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedLoadDoesNotFallThroughToAPullOrInspection()
    {
        using var fixture = new ArchiveFixture();
        var calls = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) => { calls.Add([.. args]); return Task.FromResult(new PodmanCommandResult(1, "", "native failure")); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store(runner, fixture.Entry).ResolveCachedAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "image", "load" }, calls.Select(args => args[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialIdentityOrArchiveTraversalIsRejectedBeforePodman(bool traversal)
    {
        using var fixture = new ArchiveFixture();
        bool called = false;
        var runner = new PodmanCommandRunner((_, _) => { called = true; throw new InvalidOperationException("must not execute"); });
        RuntimeImageEntry entry = traversal ? fixture.Entry with { Archive = "../outside.oci" } : fixture.Entry with { Platform = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store(runner, entry).ResolveCachedAsync(source, TestContext.Current.CancellationToken));
        Assert.False(called);
    }

    [Fact]
    public async Task MutableLocalTagsRequireAnExplicitSignedConfigBinding()
    {
        using var fixture = new ArchiveFixture();
        bool called = false;
        var runner = new PodmanCommandRunner((_, _) => { called = true; throw new InvalidOperationException("must not inspect mutable tag"); });
        var entry = new RuntimeImageEntry("local", "localhost/kautuka-release:latest", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store(runner, entry).ResolveCachedAsync(entry.Image, TestContext.Current.CancellationToken));
        Assert.False(called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedSourceRolesMustAgreeOnTheConfigBinding(bool conflict)
    {
        using var fixture = new ArchiveFixture();
        RuntimeImageEntry first = fixture.Entry;
        RuntimeImageEntry second = first with { Id = "second-role", Archive = "another.oci", LocalImageId = conflict ? "sha256:" + new string('c', 64) : localId };
        bool called = false;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            called = true;
            return Task.FromResult(new PodmanCommandResult(0, args[1] == "inspect" ? Inspection(localId, "amd64") : "", ""));
        });
        var store = new PodmanImageArchiveStore(runner, Path.GetDirectoryName(fixture.Archive)!, new RuntimeImageAllowlist(1, [first, second]), NullLogger<PodmanImageArchiveStore>.Instance);
        if (conflict)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ResolveCachedAsync(source, TestContext.Current.CancellationToken));
            Assert.False(called);
        }
        else
        {
            Assert.Equal(localId, (await store.ResolveCachedAsync(source, TestContext.Current.CancellationToken)).LocalImageId);
            Assert.True(called);
        }
    }

    private static string Inspection(string id, string architecture) => new JsonArray((JsonNode)new JsonObject
    {
        ["Id"] = id,
        ["Os"] = "linux",
        ["Architecture"] = architecture,
    }).ToJsonString();

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-oci-" + Guid.NewGuid().ToString("N"));
        internal string Archive => Path.Combine(root, "image.oci");
        internal RuntimeImageEntry Entry { get; }
        internal ArchiveFixture()
        {
            Directory.CreateDirectory(root);
            byte[] bytes = "archive fixture"u8.ToArray();
            File.WriteAllBytes(Archive, bytes);
            Entry = new("fixture", source, "image.oci", Convert.ToHexStringLower(SHA256.HashData(bytes)), "linux/amd64", localId);
        }
        internal PodmanImageArchiveStore Store(PodmanCommandRunner runner, RuntimeImageEntry entry)
            => new(runner, root, new RuntimeImageAllowlist(1, [entry]), NullLogger<PodmanImageArchiveStore>.Instance);
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
