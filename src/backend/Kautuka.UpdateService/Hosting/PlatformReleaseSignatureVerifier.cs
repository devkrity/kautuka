using Kautuka.Telemetry.Security;

namespace Kautuka.UpdateService.Hosting;

/// <summary>Compatibility shim; prefer <see cref="ReleaseManifestSignatureVerifier"/>.</summary>
internal static class PlatformReleaseSignatureVerifier
{
    public static bool TryVerify(byte[] manifestBytes, string signatureContent, string publicKeyPem, out string? error)
        => ReleaseManifestSignatureVerifier.TryVerify(manifestBytes, signatureContent, publicKeyPem, out error);
}
