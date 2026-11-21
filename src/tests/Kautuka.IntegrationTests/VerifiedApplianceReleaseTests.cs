using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Kautuka.Manager.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class VerifiedApplianceReleaseTests
{
    private static readonly string[] roles = ["credentialshost", "channelhost", "gateway", "webhost", "updateservice"];

    [Fact]
    public void AcceptsTheSignedSelectedReleaseWithItsFivePinnedImages()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        VerifiedApplianceRelease release = VerifiedApplianceRelease.Verify(Sign(key, BuildManifest()));

        Assert.Equal("v1.2.3-preview.1", release.ReleaseId);
        Assert.Equal("1.2.3-preview.1", release.Version);
        Assert.Equal(1, release.Deployment.HookAbi);
        Assert.Equal(roles.Length, release.Deployment.Images.Count);
        Assert.Equal(Image("gateway"), release.Deployment.Images["gateway"]);
    }

    [Fact]
    public void RejectsTamperedBytesAndAKeyOtherThanTheInstalledTrustKey()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string original = BuildManifest();
        TrustedApplianceReleaseInput input = Sign(key, original);
        byte[] tampered = Encoding.UTF8.GetBytes(original.Replace(Image("gateway"), Image("other"), StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => VerifiedApplianceRelease.Verify(input with { ManifestBytes = tampered }));
        Assert.Throws<InvalidDataException>(() => VerifiedApplianceRelease.Verify(input with { PublicKeyPem = other.ExportSubjectPublicKeyInfoPem() }));
    }

    [Fact]
    public void RejectsReplayAgainstAnotherVersionDirectoryOrReleaseIdentity()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TrustedApplianceReleaseInput input = Sign(key, BuildManifest());

        Assert.Throws<InvalidDataException>(() => VerifiedApplianceRelease.Verify(input with { VersionDirectoryName = "1.2.4" }));
        Assert.Throws<InvalidDataException>(() => VerifiedApplianceRelease.Verify(input with { InstalledReleaseId = "v1.2.4" }));
    }

    [Fact]
    public void RejectsInvalidIdentityAndUnsupportedDeploymentEvenWhenTheSignatureIsValid()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string valid = BuildManifest();
        string[] invalid =
        [
            BuildManifest(hookAbi: 2),
            BuildManifest(includeDeployment: false),
            ChangeManifest(valid, manifest => manifest["schemaVersion"] = 3),
            ChangeManifest(valid, manifest => manifest["ReleaseId"] = "v1.2.3-preview.1"),
            ChangeManifest(valid, manifest => manifest["releaseTag"] = "v1.2.4"),
            ChangeManifest(valid, manifest => manifest["signature"]!["format"] = "hex"),
        ];

        foreach (string manifest in invalid)
        {
            Assert.NotEqual(valid, manifest);
            Assert.Throws<InvalidDataException>(() => VerifiedApplianceRelease.Verify(Sign(key, manifest)));
        }
    }

    private static TrustedApplianceReleaseInput Sign(ECDsa key, string manifest)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(manifest);
        string signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return new(bytes, signature, key.ExportSubjectPublicKeyInfoPem(), "v1.2.3-preview.1", "1.2.3-preview.1");
    }

    private static string Image(string role) => $"ghcr.io/devkrity/kautuka-{role}@sha256:{new string('a', 64)}";

    private static string ChangeManifest(string json, Action<JsonObject> change)
    {
        JsonObject manifest = JsonNode.Parse(json)!.AsObject();
        change(manifest);
        return manifest.ToJsonString();
    }

    private static string BuildManifest(int hookAbi = 1, bool includeDeployment = true)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["releaseId"] = "v1.2.3-preview.1",
            ["releaseTag"] = "v1.2.3-preview.1",
            ["version"] = "1.2.3-preview.1",
            ["sourceSha"] = "0123456789abcdef0123456789abcdef01234567",
            ["signature"] = new JsonObject
            {
                ["algorithm"] = "ecdsa-p256-sha256",
                ["format"] = "base64",
            },
        };

        if (includeDeployment)
        {
            var services = new JsonObject();
            foreach (string role in roles)
                services.Add(role, new JsonObject { ["image"] = Image(role) });

            manifest["applianceDeployment"] = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["hookAbi"] = hookAbi,
                ["services"] = services,
            };
        }

        return manifest.ToJsonString();
    }
}
