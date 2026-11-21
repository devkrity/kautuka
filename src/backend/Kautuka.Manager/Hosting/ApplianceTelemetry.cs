using System.Security.Cryptography;
using System.Text;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Telemetry.Telemetry;

namespace Kautuka.Manager.Hosting;

internal static class ApplianceTelemetry
{
    internal const string SecretName = "kautuka-appliance-observability";
    internal const string StateName = "observability";

    internal static async Task<string> EnsureAuthorizationAsync(PodmanCommandRunner podman, string stateRoot, CancellationToken token)
    {
        PodmanCommandResult exists = await podman.RunAsync(["secret", "exists", SecretName], 10, token).ConfigureAwait(false);
        if (exists.ExitCode == 1)
        {
            string data = Path.Combine(stateRoot, StateName);
            if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any())
                throw new InvalidOperationException("The appliance observability secret is missing while its data directory is populated.");

            string generated = "A!9" + RandomNumberGenerator.GetHexString(48, lowercase: true);
            byte[] bytes = Encoding.ASCII.GetBytes(generated);
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                PodmanCommandResult created = await podman.RunAsync(["secret", "create", SecretName, "-"], 10, token,
                    standardInput: input).ConfigureAwait(false);
                if (created.ExitCode != 0)
                    throw new InvalidOperationException("Could not provision the appliance observability secret.");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return BuildAuthorization(generated);
        }
        if (exists.ExitCode != 0)
            throw new InvalidOperationException("Could not check the appliance observability secret.");

        PodmanCommandResult retained = await podman.RunAsync(
            ["secret", "inspect", "--showsecret", "--format", "{{.SecretData}}", SecretName], 10, token).ConfigureAwait(false);
        if (retained.ExitCode != 0)
            throw new InvalidOperationException("Could not read the appliance observability secret.");
        return BuildAuthorization(retained.StdOut.TrimEnd('\r', '\n'));
    }

    private static string BuildAuthorization(string password)
    {
        if (password.Length != 51 || !password.StartsWith("A!9", StringComparison.Ordinal)
            || !password.Skip(3).All(static c => c is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new InvalidOperationException("The appliance observability secret has an invalid format.");
        return Convert.ToBase64String(Encoding.ASCII.GetBytes($"{KautukaTelemetryDefaults.DefaultOpenObserveRootUserEmail}:{password}"));
    }
}
