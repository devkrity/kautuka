using System.Security.Cryptography;
using System.Text;

using Kautuka.UpdateService.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class PlatformReleaseSignatureVerifierTests
{
    [Fact]
    public void TryVerifyAcceptsValidDetachedSignature()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}\n");

        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        string signature = Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256));

        bool isValid = PlatformReleaseSignatureVerifier.TryVerify(payload, signature, publicKeyPem, out string? error);

        Assert.True(isValid);
        Assert.Null(error);
    }

    [Fact]
    public void TryVerifyAcceptsValidDerEncodedDetachedSignature()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}\n");

        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        string signature = Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        bool isValid = PlatformReleaseSignatureVerifier.TryVerify(payload, signature, publicKeyPem, out string? error);

        Assert.True(isValid);
        Assert.Null(error);
    }

    [Fact]
    public void TryVerifyRejectsModifiedPayload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}\n");
        byte[] modifiedPayload = Encoding.UTF8.GetBytes("{\"schemaVersion\":3}\n");

        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        string signature = Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256));

        bool isValid = PlatformReleaseSignatureVerifier.TryVerify(modifiedPayload, signature, publicKeyPem, out string? error);

        Assert.False(isValid);
        Assert.Equal("Platform release signature verification failed.", error);
    }
}
