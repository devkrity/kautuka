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
    public void InspectionBindsRepositoryDigestRoleAndPlatformToLocalImageId()
    {
        string imageId = new('b', 64);
        string valid = $$"""
            [{
              "Id": "{{imageId}}",
              "Digest": "{{Digest}}",
              "RepoDigests": ["{{Reference}}"],
              "Os": "linux",
              "Architecture": "amd64",
              "Config": {
                "Labels": { "io.kautuka.service.role": "gateway" }
              }
            }]
            """;
        var references = new Dictionary<string, string> { ["gateway"] = Reference };
        Assert.Equal($"sha256:{imageId}", ApplianceRoleImageImporter.ReadInstalledImageIds(valid, references, "amd64")["gateway"]);
        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ReadInstalledImageIds(valid,
            new Dictionary<string, string> { ["webhost"] = Reference }, "amd64"));
        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ReadInstalledImageIds(valid, references, "arm64"));
        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ReadInstalledImageIds(
            valid.Replace("registry.example", "other.example", StringComparison.Ordinal), references, "amd64"));
        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ReadInstalledImageIds(
            valid.Replace(imageId, "missing", StringComparison.Ordinal), references, "amd64"));
        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ReadInstalledImageIds("{}", references, "amd64"));

        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ReadInstalledImageIds(
            valid, references, "amd64", requireVerificationFence: true));
        string fenced = valid.Replace(
            """{ "io.kautuka.service.role": "gateway" }""",
            """{ "io.kautuka.service.role": "gateway", "io.kautuka.service.appliance-fence": "1" }""",
            StringComparison.Ordinal);
        Assert.Equal($"sha256:{imageId}", ApplianceRoleImageImporter.ReadInstalledImageIds(
            fenced, references, "amd64", requireVerificationFence: true)["gateway"]);
        Assert.Throws<InvalidDataException>(() => ApplianceRoleImageImporter.ReadInstalledImageIds(
            fenced.Replace("""appliance-fence": "1" """, """appliance-fence": "0" """, StringComparison.Ordinal),
            references, "amd64", requireVerificationFence: true));
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
