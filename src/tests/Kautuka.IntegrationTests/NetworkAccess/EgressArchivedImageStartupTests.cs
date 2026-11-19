using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class EgressArchivedImageStartupTests
{
    [Fact]
    public void CanaryImageUsesTheReleaseManifest()
    {
        RuntimeImageAllowlist images = RuntimeImageAllowlistLoader.Load(
            Path.Combine(AppContext.BaseDirectory, RuntimeImageAllowlistLoader.FileName), requirePresent: true);
        Assert.Equal(Assert.Single(images.Images, image => image.Id == "kautuka-base").Image,
            EgressTopologyManager.CanaryBasePinnedImage);
    }
    [Fact]
    public void X64EgressImagesUsePlatformManifestsInTheArchiveInventory()
    {
        RuntimeImageAllowlist images = RuntimeImageAllowlistLoader.Load(
            Path.Combine(AppContext.BaseDirectory, RuntimeImageAllowlistLoader.FileName), requirePresent: true);
        Assert.Equal(EgressTopologyManager.EnvoyX64Image, Assert.Single(images.Images, i => i.Id == "envoy").Image);
        Assert.Equal(EgressTopologyManager.CoreDnsX64Image, Assert.Single(images.Images, i => i.Id == "coredns").Image);
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "compliance", "third-party-components.json"))) root = root.Parent;
        Assert.NotNull(root);
        using JsonDocument catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "compliance", "third-party-components.json")));
        JsonElement components = catalog.RootElement.GetProperty("components");
        Assert.Equal(EgressTopologyManager.EnvoyX64Image, components.EnumerateArray().Single(i => i.GetProperty("id").GetString() == "envoy").GetProperty("image").GetString());
        Assert.Equal(EgressTopologyManager.CoreDnsX64Image, components.EnumerateArray().Single(i => i.GetProperty("id").GetString() == "coredns").GetProperty("image").GetString());
    }
    [Fact]
    public void EgressImagePinsUseTheManifestForBothArchitectures()
    {
        RuntimeImageAllowlist images = RuntimeImageAllowlistLoader.Load(
            Path.Combine(AppContext.BaseDirectory, RuntimeImageAllowlistLoader.FileName), requirePresent: true);
        foreach (string id in new[] { "envoy", "coredns" })
        {
            RuntimeImageEntry entry = Assert.Single(images.Images, image => image.Id == id);
            Assert.Equal(entry.Image, EgressTopologyManager.SelectRuntimeImage(id, System.Runtime.InteropServices.Architecture.X64));
            Assert.NotNull(entry.Arm64Image);
            Assert.Equal(entry.Arm64Image, EgressTopologyManager.SelectRuntimeImage(id, System.Runtime.InteropServices.Architecture.Arm64));
            Assert.NotEqual(entry.Image, entry.Arm64Image);
        }
    }

    [Fact]
    public void PublishedBoundaryImageMatchesThePackagedArchiveInventory()
    {
        RuntimeImageAllowlist images = RuntimeImageAllowlistLoader.Load(
            Path.Combine(AppContext.BaseDirectory, RuntimeImageAllowlistLoader.FileName), requirePresent: true);
        RuntimeImageEntry entry = Assert.Single(images.Images, image => image.Id == "egress-boundary");
        Assert.Equal(EgressTopologyManager.BoundaryInitImage, entry.Image);
        Assert.Equal("egress-boundary.tar", entry.Archive);
    }

    [Fact]
    public async Task ProductionInitializationLoadsArchivedEgressImagesBeforeCreatingNetworks()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-archived-egress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:Mode"] = "dev",
                ["Kautuka:ContainerStateRoot"] = root,
            }).Build();
            var paths = new WorkspaceRuntimePaths(configuration, new TestHostEnvironment(), NullLogger<WorkspaceRuntimePaths>.Instance);
            string envoyArchive = Path.Combine(paths.OciImagesRoot, "envoy.tar");
            string dnsArchive = Path.Combine(paths.OciImagesRoot, "coredns.tar");
            string boundaryArchive = Path.Combine(paths.OciImagesRoot, "egress-boundary.tar");
            await File.WriteAllTextAsync(envoyArchive, "fixture", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(dnsArchive, "fixture", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(boundaryArchive, "fixture", TestContext.Current.CancellationToken);
            var calls = new List<string>();
            var podman = new PodmanCommandRunner((args, _) =>
            {
                calls.Add(string.Join(" ", args));
                int code = args[0] == "network" || args.SequenceEqual(["image", "exists", EgressTopologyManager.EnvoyPinnedImage])
                    || args.SequenceEqual(["image", "exists", EgressTopologyManager.CoreDnsPinnedImage])
                    || args.SequenceEqual(["image", "exists", EgressTopologyManager.BoundaryInitImage]) ? 1 : 0;
                return Task.FromResult(new PodmanCommandResult(code, string.Empty, string.Empty));
            });
            var images = new RuntimeImageAllowlist(1,
            [
                new RuntimeImageEntry("envoy", EgressTopologyManager.EnvoyPinnedImage, "envoy.tar"),
                new RuntimeImageEntry("coredns", EgressTopologyManager.CoreDnsPinnedImage, "coredns.tar"),
                new RuntimeImageEntry("egress-boundary", EgressTopologyManager.BoundaryInitImage, "egress-boundary.tar"),
            ]);
            var registry = new WorkloadRegistry();
            var services = new ServiceCollection();
            services.AddSingleton(podman);
            services.AddSingleton(new WorkloadAddressAllocator(Path.Combine(root, "ip.json")));
            services.AddSingleton(new WorkloadPostStartVerifier(podman));
            services.AddSingleton<IWorkloadRegistry>(registry);
            services.AddSingleton(new PodmanImageArchiveStore(podman, paths, images, NullLogger<PodmanImageArchiveStore>.Instance));
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using EgressTopologyManager manager = ActivatorUtilities.CreateInstance<EgressTopologyManager>(provider, root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InitializeProductionAsync(registry, false, TestContext.Current.CancellationToken));
            int network = calls.FindIndex(call => call.StartsWith("network ", StringComparison.Ordinal));
            int envoy = calls.IndexOf("load -i " + envoyArchive);
            int dns = calls.IndexOf("load -i " + dnsArchive);
            int boundary = calls.IndexOf("load -i " + boundaryArchive);
            Assert.True(envoy >= 0 && dns >= 0 && boundary >= 0 && network > envoy && network > dns && network > boundary, string.Join("; ", calls));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NacTest";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
