using System.Security.Cryptography;

namespace Kautuka.Telemetry.Security;

/// <summary>
/// Fail-closed SHA-256 checks for downloaded release artifacts.
/// </summary>
public static class ReleaseArtifactHashVerifier
{
    public static void Validate(
        string packagePath,
        string? expectedSha256,
        long expectedSize,
        bool requireHash,
        string artifactName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);

        FileInfo packageInfo = new(packagePath);
        if (!packageInfo.Exists)
        {
            throw new InvalidOperationException($"Downloaded artifact '{artifactName}' was not found at '{packagePath}'.");
        }

        if (expectedSize > 0 && packageInfo.Length != expectedSize)
        {
            throw new InvalidOperationException(
                $"Downloaded artifact size mismatch for '{artifactName}'. Expected {expectedSize} bytes but found {packageInfo.Length} bytes.");
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            if (requireHash)
            {
                throw new InvalidOperationException(
                    $"Artifact '{artifactName}' is missing SHA-256. Fail-closed verification rejects unsigned hashes.");
            }

            return;
        }

        using FileStream stream = File.OpenRead(packagePath);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Downloaded artifact hash mismatch for '{artifactName}'. Expected {expectedSha256} but found {actualHash}.");
        }
    }
}
