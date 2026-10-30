using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class RuntimeImageAllowlistTests
{
    [Fact]
    public void LocalImagesAreTrustedWithoutAnAllowlist()
    {
        ContainerImageProvenancePolicy.EnsureTrusted("kautuka-base:latest", "sandbox");
        ContainerImageProvenancePolicy.EnsureTrusted("localhost/kautuka-pkgs-proxy:local", "sidecar");
    }

    [Fact]
    public void MutableRegistryTagsAreRejected()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ContainerImageProvenancePolicy.EnsureTrusted("quay.io/fedora/fedora-minimal:44", "sandbox"));
        Assert.Contains("@sha256:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageReferenceWithANewlineIsRejected()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ContainerImageProvenancePolicy.EnsureTrusted("kautuka-base:latest\nRUN true", "Connector 'custom'"));
        Assert.Contains("single line", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortRegistryNamesAreNotTreatedAsLocalImages()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ContainerImageProvenancePolicy.EnsureTrusted("python:latest", "Connector 'custom'"));
        Assert.Contains("@sha256:", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(
            () => ContainerImageProvenancePolicy.EnsureTrusted("alpine", "Connector 'custom'"));
    }

    [Fact]
    public void ListedInfrastructureDigestIsAdmitted()
    {
        RuntimeImageAllowlist allowlist = LoadCommittedAllowlist();
        string listedImage = allowlist.Images[0].Image;
        ContainerImageProvenancePolicy.EnsureTrusted(
            listedImage,
            "MCPB base",
            allowlist.ImageReferences,
            requireListed: true);
    }

    [Fact]
    public void UnlistedInfrastructureDigestIsRejectedWhenAllowlistIsLoaded()
    {
        RuntimeImageAllowlist allowlist = LoadCommittedAllowlist();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ContainerImageProvenancePolicy.EnsureTrusted(
                "quay.io/fedora/fedora-minimal@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "sandbox",
                allowlist.ImageReferences,
                requireListed: true));
        Assert.Contains("allowlist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserConnectorDigestsRemainAdmittedWithoutAllowlistMembership()
    {
        RuntimeImageAllowlist allowlist = LoadCommittedAllowlist();
        ContainerImageProvenancePolicy.EnsureTrusted(
            "ghcr.io/example/tool@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "Connector 'custom'",
            allowlist.ImageReferences,
            requireListed: false);
    }

    [Fact]
    public void CommittedAllowlistCoversSandboxAndSidecarPins()
    {
        RuntimeImageAllowlist allowlist = LoadCommittedAllowlist();
        string repoRoot = FindRepoRoot();
        string dockerfile = File.ReadAllText(Path.Combine(repoRoot, "src", "images", "kautuka-base", "Dockerfile"));
        string pkgsProxy = File.ReadAllText(Path.Combine(repoRoot, "src", "images", "kautuka-pkgs-proxy", "Dockerfile"));
        string sidecarsJson = File.ReadAllText(Path.Combine(repoRoot, "src", "backend", "Kautuka.Manager", "sidecars.json"));

        Assert.Contains("ARG FEDORA_IMAGE=quay.io/fedora/fedora-minimal:44", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("quay.io/fedora/fedora-minimal@sha256:", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("pip install", dockerfile, StringComparison.Ordinal);
        Assert.Contains("0ce210831380ab75a5fec0d3fb6b5af0958898016b2cae51cc2fa11057d2f77e", dockerfile, StringComparison.Ordinal);
        Assert.Contains("7035608168e106375b36d0c818d537a889c51a8625fe7f8f7cad5e62b947c368", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain(allowlist.Images, entry => entry.Id == "fedora-minimal");
        Assert.Contains(allowlist.Images, entry => pkgsProxy.Contains(entry.Image, StringComparison.Ordinal));
        Assert.Contains(allowlist.Images, entry => sidecarsJson.Contains(entry.Image, StringComparison.Ordinal));
    }

    [Fact]
    public void ReleasedKautukaBaseDigestReplacesLocalAndFedoraMinimalBases()
    {
        const string released = "ghcr.io/devkrity/kautuka-base@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var allowlist = new RuntimeImageAllowlist(1,
        [
            new RuntimeImageEntry("kautuka-base", released, null),
            new RuntimeImageEntry("openobserve", "public.ecr.aws/zinclabs/openobserve@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", null),
        ]);

        Assert.Equal(released, allowlist.SelectReleasedSandboxImage("kautuka-base:latest"));
        Assert.Equal(released, allowlist.SelectReleasedSandboxImage("localhost/kautuka-base:latest"));
        Assert.Equal(released, allowlist.SelectReleasedSandboxImage("quay.io/fedora/fedora-minimal@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", "node"));
        Assert.Equal(released, allowlist.SelectReleasedSandboxImage("ghcr.io/example/tool:latest", "uv"));
        Assert.Equal(released, allowlist.SelectReleasedSandboxImage("ghcr.io/example/tool:latest", "binary"));
        Assert.Equal(
            "public.ecr.aws/zinclabs/openobserve@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            allowlist.SelectReleasedSandboxImage("public.ecr.aws/zinclabs/openobserve@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        Assert.Equal("kautuka-base:latest", RuntimeImageAllowlist.Empty.SelectReleasedSandboxImage("kautuka-base:latest"));
        Assert.Equal(
            "quay.io/fedora/fedora-minimal:44",
            RuntimeImageAllowlist.Empty.SelectReleasedSandboxImage("quay.io/fedora/fedora-minimal:44", "node"));
        Assert.Equal(
            "localhost/kautuka-base:latest",
            RuntimeImageAllowlist.Empty.SelectReleasedSandboxImage(
                "quay.io/fedora/fedora-minimal@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "uv"));
    }

    [Fact]
    public void LoaderRejectsMissingAllowlistWhenRequired()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"kautuka-runtime-images-{Guid.NewGuid():N}.json");
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => RuntimeImageAllowlistLoader.Load(missing, requirePresent: true));
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeImageAllowlist LoadCommittedAllowlist()
    {
        string path = Path.Combine(FindRepoRoot(), "src", "backend", "Kautuka.Manager", RuntimeImageAllowlistLoader.FileName);
        RuntimeImageAllowlist allowlist = RuntimeImageAllowlistLoader.Load(path, requirePresent: true);
        Assert.NotEmpty(allowlist.Images);
        return allowlist;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "backend", "Kautuka.Manager", RuntimeImageAllowlistLoader.FileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
