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
    public void X64EgressImagesUsePlatformManifestsInTheArchiveInventory()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        RuntimeImageAllowlist images = RuntimeImageAllowlistLoader.Load(
            Path.Combine(AppContext.BaseDirectory, RuntimeImageAllowlistLoader.FileName), requirePresent: true);
        Assert.Equal("docker.io/envoyproxy/envoy@sha256:16e0d4dac937ffa34103aa6ba1013dc2030b9de2a249273c8d23c7c5c2989664", EgressTopologyManager.EnvoyPinnedImage);
        Assert.Equal("docker.io/coredns/coredns@sha256:1ba6f47265602e2e50a9c4669e3a955e4298a0d30dc82f39293d4bf1a851e0ff", EgressTopologyManager.CoreDnsPinnedImage);
        Assert.Equal(EgressTopologyManager.EnvoyPinnedImage, Assert.Single(images.Images, i => i.Id == "envoy").Image);
        Assert.Equal(EgressTopologyManager.CoreDnsPinnedImage, Assert.Single(images.Images, i => i.Id == "coredns").Image);
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
