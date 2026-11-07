using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class RegistryImageIdentityTests
{
    private const string repository = "quay.io/example/connector";
    private const string imageType = "application/vnd.oci.image.manifest.v1+json";
    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static byte[] Config(string architecture = "amd64") => Json(new { os = "linux", architecture });
    private static byte[] Manifest(byte[] config) => Json(new { schemaVersion = 2, mediaType = imageType, config = new { digest = Digest(config) }, layers = Array.Empty<object>() });
    private static object Descriptor(byte[] manifest, string architecture = "amd64") => new { mediaType = imageType, digest = Digest(manifest), platform = new { os = "linux", architecture } };
    private static byte[] Index(params object[] descriptors) => Json(new { schemaVersion = 2, mediaType = "application/vnd.oci.image.index.v1+json", manifests = descriptors });

    [Theory]
    [InlineData("busybox")]
    [InlineData("example/connector")]
    [InlineData("https://quay.io/example/connector")]
    [InlineData("quay.io/example/connector\0")]
    [InlineData("quay.io/example/connector:latest")]
    public void InvalidSourceIsRejectedBeforeRegistryIo(string repositoryName)
        => Assert.Throws<InvalidOperationException>(() => RegistryImageIdentity.ValidateSource(repositoryName + "@sha256:" + new string('a', 64), "linux/amd64"));

    [Fact]
    public void IndexMembershipAndConfigAreBoundToTheOriginalSource()
    {
        byte[] config = Config();
        byte[] manifest = Manifest(config);
        byte[] index = Index(Descriptor(manifest), Descriptor(Manifest(Config("arm64")), "arm64"));
        string source = repository + "@" + Digest(index);
        RegistryImageIdentity identity = RegistryImageIdentity.Select(source, index, "linux/amd64");
        Assert.Equal(source, identity.SourceReference);
        Assert.Equal(repository + "@" + Digest(manifest), identity.SelectedReference);
        Assert.Equal(Digest(config), identity.VerifyConfigIdentity(manifest, config));
        Assert.Throws<InvalidOperationException>(() => identity.VerifyConfigIdentity(Manifest(Config("arm64")), Config("arm64")));
        Assert.Throws<InvalidOperationException>(() => identity.VerifyConfigIdentity(manifest, Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void SingleManifestStillRequiresTheExpectedConfigPlatform()
    {
        byte[] config = Config("arm64");
        byte[] manifest = Manifest(config);
        string source = repository + "@" + Digest(manifest);
        RegistryImageIdentity identity = RegistryImageIdentity.Select(source, manifest, "linux/amd64");
        Assert.Equal(source, identity.SelectedReference);
        Assert.Throws<InvalidOperationException>(() => identity.VerifyConfigIdentity(manifest, config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrAmbiguousPlatformIsRejected(bool ambiguous)
    {
        byte[] manifest = Manifest(Config());
        byte[] index = ambiguous ? Index(Descriptor(manifest), Descriptor(manifest)) : Index(Descriptor(manifest, "arm64"));
        Assert.Throws<InvalidOperationException>(() => RegistryImageIdentity.Select(repository + "@" + Digest(index), index, "linux/amd64"));
    }

    [Fact]
    public void ChangedBytesCannotBorrowThePinnedDigest()
    {
        byte[] manifest = Manifest(Config());
        byte[] changed = [.. manifest, (byte)' '];
        Assert.Throws<InvalidOperationException>(() => RegistryImageIdentity.Select(repository + "@" + Digest(manifest), changed, "linux/amd64"));
        Assert.Throws<InvalidOperationException>(() => RegistryImageIdentity.Select(repository + ":latest", manifest, "linux/amd64"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("changed-config")]
    [InlineData("duplicate")]
    [InlineData("symlink")]
    [InlineData("extra-image")]
    public async Task AcquiredArchiveMustPreserveTheSelectedManifestAndConfig(string scenario)
    {
        byte[] config = Config();
        byte[] manifest = Manifest(config);
        string source = repository + "@" + Digest(manifest);
        RegistryImageIdentity identity = RegistryImageIdentity.Select(source, manifest, "linux/amd64");
        string path = Path.Combine(Path.GetTempPath(), "kautuka-acquired-" + Guid.NewGuid().ToString("N") + ".oci");
        try
        {
            using (FileStream file = File.Create(path))
            using (var writer = new TarWriter(file))
            {
                async Task Write(string name, byte[] bytes)
                {
                    using var data = new MemoryStream(bytes);
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data }, TestContext.Current.CancellationToken);
                }
                await Write("index.json", Index(scenario == "extra-image" ? [Descriptor(manifest), Descriptor(manifest)] : [Descriptor(manifest)]));
                await Write("blobs/sha256/" + Digest(manifest)[7..], manifest);
                string configName = "blobs/sha256/" + Digest(config)[7..];
                if (scenario == "symlink") await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, configName) { LinkName = "outside.json" }, TestContext.Current.CancellationToken);
                else await Write(configName, scenario == "changed-config" ? Encoding.UTF8.GetBytes("{}") : config);
                if (scenario == "duplicate") await Write(configName, config);
            }
            if (scenario == "valid") Assert.Equal(Digest(config), await identity.VerifyArchiveAsync(path, TestContext.Current.CancellationToken));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => identity.VerifyArchiveAsync(path, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(path); }
    }
}
