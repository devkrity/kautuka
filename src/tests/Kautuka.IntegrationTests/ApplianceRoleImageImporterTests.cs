using System.Formats.Tar;
using System.Text;

using Kautuka.Manager.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ApplianceRoleImageImporterTests
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Reference = "registry.example/kautuka-gateway@" + Digest;

    [Fact]
    public void IndexRequiresOneSignedManifest()
    {
        string index = "{\"schemaVersion\":2,\"manifests\":[{\"digest\":\"" + Digest + "\"}]}";
        using MemoryStream valid = Archive(index);
        ApplianceRoleImageImporter.ValidateArchiveIndex(valid, Reference);
        using MemoryStream wrong = Archive("""{"schemaVersion":2,"manifests":[{"digest":"sha256:bad"}]}""");
        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ValidateArchiveIndex(wrong, Reference));
        using MemoryStream multiple = Archive("{\"schemaVersion\":2,\"manifests\":[{\"digest\":\"" + Digest + "\"},{\"digest\":\"" + Digest + "\"}]}");
        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ValidateArchiveIndex(multiple, Reference));
        using MemoryStream duplicate = Archive(index, duplicate: true);
        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ValidateArchiveIndex(duplicate, Reference));
    }

    [Fact]
    public void InspectionBindsRepositoryDigestRoleAndPlatform()
    {
        string valid = "[{\"Digest\":\"" + Digest + "\",\"RepoDigests\":[\"" + Reference + "\"],\"Os\":\"linux\",\"Architecture\":\"amd64\",\"Config\":{\"Labels\":{\"io.kautuka.service.role\":\"gateway\"}}}]";
        Assert.True(ApplianceRoleImageImporter.InspectMatches(valid, Reference, "gateway", "amd64"));
        Assert.False(ApplianceRoleImageImporter.InspectMatches(valid, Reference, "webhost", "amd64"));
        Assert.False(ApplianceRoleImageImporter.InspectMatches(valid, Reference, "gateway", "arm64"));
        Assert.False(ApplianceRoleImageImporter.InspectMatches(valid.Replace("registry.example", "other.example", StringComparison.Ordinal), Reference, "gateway", "amd64"));
        Assert.False(ApplianceRoleImageImporter.InspectMatches("{}", Reference, "gateway", "amd64"));
    }

    private static MemoryStream Archive(string index, bool duplicate = false)
    {
        var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            using var first = new MemoryStream(Encoding.UTF8.GetBytes(index));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "index.json") { DataStream = first });
            if (duplicate)
            {
                using var second = new MemoryStream(Encoding.UTF8.GetBytes(index));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "index.json") { DataStream = second });
            }
        }
        output.Position = 0;
        return output;
    }
}
