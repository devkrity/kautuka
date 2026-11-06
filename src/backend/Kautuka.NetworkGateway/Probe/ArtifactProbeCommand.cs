using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kautuka.NetworkGateway.Probe;

/// <summary>Offline fixture verifier. Trust provisioning belongs to the root-owned fixture caller.</summary>
internal static class ArtifactProbeCommand
{
    public static int Run(string manifestPath, string signaturePath, string trustedKeyPath)
    {
        try
        {
            byte[] manifest = ReadBounded(manifestPath, 16384);
            string signature = Encoding.UTF8.GetString(ReadBounded(signaturePath, 4096));
            string trustedKey = Encoding.UTF8.GetString(ReadBounded(trustedKeyPath, 4096));
            VerifiedArtifactPlan plan = VerifiedArtifactPlan.Verify(manifest, signature, trustedKey);
            Console.WriteLine(JsonSerializer.Serialize(plan, VerifiedArtifactPlanJsonContext.Default.VerifiedArtifactPlan));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or CryptographicException
            or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Artifact fixture rejected: {error.Message}");
            return 1;
        }
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[maximum + 1];
        int count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (count > maximum)
        {
            throw new InvalidDataException("Artifact fixture input exceeds its size limit.");
        }
        return buffer.AsSpan(0, count).ToArray();
    }
}
