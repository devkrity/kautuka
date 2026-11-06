using System.Security.Cryptography;
using System.Text;

using Kautuka.NetworkGateway.Probe;

namespace Kautuka.NetworkGateway.Tests;

public sealed class VerifiedArtifactPlanTests
{
    private const string Manifest = """
        {"schemaVersion":1,"releaseId":"ktng-0123456789abcdef0123456789abcdef","registry":"10.231.252.2:20443","repository":"ktng/probe","platform":"linux/amd64","manifestDigest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","configurationDigest":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","compatibility":"ktng-probe-v1"}
        """;

    [Fact]
    public void VerifiedPlanBindsImmutablePlatformManifestAndConfiguration()
    {
        using ECDsa trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] bytes = Encoding.UTF8.GetBytes(Manifest);
        VerifiedArtifactPlan plan = VerifiedArtifactPlan.Verify(bytes, Sign(trusted, bytes), trusted.ExportSubjectPublicKeyInfoPem());
        Assert.Equal("10.231.252.2:20443/ktng/probe@sha256:" + new string('a', 64), plan.ImageReference);
        Assert.Equal("sha256:" + new string('b', 64), plan.ConfigurationDigest);
    }

    [Fact]
    public void ModifiedManifestAndUntrustedSignerAreRejected()
    {
        using ECDsa trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] bytes = Encoding.UTF8.GetBytes(Manifest);
        string signature = Sign(trusted, bytes);
        Assert.Throws<InvalidDataException>(() => VerifiedArtifactPlan.Verify(
            Encoding.UTF8.GetBytes(Manifest.Replace("linux/amd64", "linux/arm64", StringComparison.Ordinal)), signature, trusted.ExportSubjectPublicKeyInfoPem()));
        Assert.Throws<InvalidDataException>(() => VerifiedArtifactPlan.Verify(bytes, Sign(attacker, bytes), trusted.ExportSubjectPublicKeyInfoPem()));
    }

    [Theory]
    [InlineData("10.231.252.2:20443", "example.com:443")]
    [InlineData("ktng/probe", "ktng/other")]
    [InlineData("linux/amd64", "linux/arm64")]
    [InlineData("ktng-probe-v1", "ktng-probe-v2")]
    [InlineData("sha256:aaaaaaaa", "sha256:AAAAAAAA")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":\"1\"")]
    public void ValidSignatureCannotExpandFixtureScope(string original, string replacement)
    {
        using ECDsa trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] bytes = Encoding.UTF8.GetBytes(Manifest.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => VerifiedArtifactPlan.Verify(bytes, Sign(trusted, bytes), trusted.ExportSubjectPublicKeyInfoPem()));
    }

    [Theory]
    [InlineData("\"schemaVersion\":1,", "\"schemaVersion\":1,\"schemaVersion\":1,")]
    [InlineData("\"schemaVersion\":1,", "\"schemaVersion\":1,\"extra\":true,")]
    public void DuplicateAndUnknownSignedFieldsAreRejected(string original, string replacement)
    {
        using ECDsa trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] bytes = Encoding.UTF8.GetBytes(Manifest.Replace(original, replacement, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => VerifiedArtifactPlan.Verify(bytes, Sign(trusted, bytes), trusted.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void OversizedAndMalformedInputsDoNotYieldAPlan()
    {
        Assert.Throws<InvalidDataException>(() => VerifiedArtifactPlan.Verify(new byte[16385], "", ""));
        Assert.Throws<InvalidDataException>(() => VerifiedArtifactPlan.Verify(Encoding.UTF8.GetBytes(Manifest), "invalid", "invalid"));
    }

    private static string Sign(ECDsa key, byte[] bytes)
        => Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
}
