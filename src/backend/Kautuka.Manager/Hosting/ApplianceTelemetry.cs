using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Telemetry.Telemetry;

namespace Kautuka.Manager.Hosting;

internal static class ApplianceTelemetry
{
    internal const string SecretName = "kautuka-appliance-observability";
    internal const string StateName = "observability";
    private const string IngestionSecretName = "kautuka-appliance-otlp";
    private const string IngestionEndpoint = "http://127.0.0.1:5080/api/default/ingestion-tokens";

    internal static async Task EnsureRootSecretAsync(PodmanCommandRunner podman, string stateRoot, CancellationToken token)
    {
        PodmanCommandResult exists = await podman.RunAsync(["secret", "exists", SecretName], 10, token).ConfigureAwait(false);
        if (exists.ExitCode == 1)
        {
            string data = Path.Combine(stateRoot, StateName);
            if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any())
                throw new InvalidOperationException("The appliance observability secret is missing while its data directory is populated.");

            await CreateSecretAsync(podman, SecretName, "A!9" + RandomNumberGenerator.GetHexString(48, lowercase: true), token)
                .ConfigureAwait(false);
        }
        else if (exists.ExitCode != 0)
            throw new InvalidOperationException("Could not check the appliance observability secret.");
    }

    internal static async Task<string?> ReadIngestionAuthorizationAsync(PodmanCommandRunner podman, CancellationToken token)
    {
        PodmanCommandResult exists = await podman.RunAsync(["secret", "exists", IngestionSecretName], 10, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (exists.ExitCode == 1) return null;
        if (exists.ExitCode != 0) throw new InvalidOperationException("Could not check the appliance ingestion secret.");
        string retained = await ReadSecretAsync(podman, IngestionSecretName, token).ConfigureAwait(false);
        return IsIngestionToken(retained) ? EncodeBasic("default", retained) : null;
    }

    internal static async Task<string?> ProvisionIngestionAuthorizationAsync(PodmanCommandRunner podman, string collectorId, CancellationToken token)
    {
        if (collectorId.Length != 64 || !collectorId.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("The appliance collector identity is invalid.");
        PodmanCommandResult inspection = await podman.RunAsync(
            ["inspect", "--format", "{{.NetworkSettings.SandboxKey}}", collectorId], 5, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        string networkNamespace = inspection.StdOut.Trim();
        if (inspection.ExitCode != 0 || !Path.IsPathFullyQualified(networkNamespace)
            || Path.GetFullPath(networkNamespace) != networkNamespace
            || Path.GetFileName(Path.GetDirectoryName(networkNamespace)) != "netns"
            || !(Path.GetFileName(networkNamespace).StartsWith("netns-", StringComparison.Ordinal)
                || Path.GetFileName(networkNamespace).StartsWith("cni-", StringComparison.Ordinal))) return null;

        // Podman's unique bind mount is resolved inside the rootless helper's mount namespace.
        string pinnedNamespace = "--net=" + networkNamespace;
        string rootAuthorization = await ReadRootAuthorizationAsync(podman, token).ConfigureAwait(false);
        (bool Exists, string? Token)? result = ParseIngestionToken(await SendAsync(create: false).ConfigureAwait(false));
        if (result is { Exists: false })
        {
            await SendAsync(create: true).ConfigureAwait(false);
            result = ParseIngestionToken(await SendAsync(create: false).ConfigureAwait(false));
        }
        if (result?.Token is not { } ingestionToken || !IsIngestionToken(ingestionToken)) return null;

        await CreateSecretAsync(podman, IngestionSecretName, ingestionToken, token, replace: true).ConfigureAwait(false);
        return EncodeBasic("default", ingestionToken);

        async Task<string?> SendAsync(bool create)
        {
            List<string> args = ["unshare", "nsenter", pinnedNamespace, "curl", "--disable", "--noproxy", "*",
                "--max-time", "3", "--max-filesize", "65536", "--silent", "--fail", "--header", "@-"];
            if (create)
                args.AddRange(["--header", "Content-Type: application/json", "--data-binary", "{\"name\":\"kautuka-appliance-otlp\"}"]);
            args.Add(IngestionEndpoint);
            byte[] header = Encoding.ASCII.GetBytes($"Authorization: Basic {rootAuthorization}\n");
            try
            {
                using var input = new MemoryStream(header, writable: false);
                PodmanCommandResult response = await podman.RunAsync(args, 5, token, standardInput: input).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return response.ExitCode == 0 ? response.StdOut : null;
            }
            finally { CryptographicOperations.ZeroMemory(header); }
        }
    }

    internal static async Task<string> ReadRootAuthorizationAsync(PodmanCommandRunner podman, CancellationToken token)
    {
        string password = await ReadSecretAsync(podman, SecretName, token).ConfigureAwait(false);
        if (password.Length != 51 || !password.StartsWith("A!9", StringComparison.Ordinal)
            || !password.Skip(3).All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException("The appliance observability secret has an invalid format.");
        return EncodeBasic(KautukaTelemetryDefaults.DefaultOpenObserveRootUserEmail, password);
    }

    private static (bool Exists, string? Token)? ParseIngestionToken(string? response)
    {
        if (response is null) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                return null;
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.GetProperty("name").GetString() == IngestionSecretName)
                    return (true, item.GetProperty("enabled").GetBoolean() ? item.GetProperty("token").GetString() : null);
            }
            return (false, null);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    private static async Task<string> ReadSecretAsync(PodmanCommandRunner podman, string name, CancellationToken token)
    {
        PodmanCommandResult retained = await podman.RunAsync(
            ["secret", "inspect", "--showsecret", "--format", "{{.SecretData}}", name], 10, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (retained.ExitCode != 0)
            throw new InvalidOperationException("Could not read the appliance telemetry secret.");
        return retained.StdOut.TrimEnd('\r', '\n');
    }

    private static async Task CreateSecretAsync(PodmanCommandRunner podman, string name, string secret, CancellationToken token, bool replace = false)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(secret);
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            string[] args = replace ? ["secret", "create", "--replace", name, "-"] : ["secret", "create", name, "-"];
            PodmanCommandResult created = await podman.RunAsync(args, 10, token,
                standardInput: input).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (created.ExitCode != 0) throw new InvalidOperationException("Could not provision the appliance telemetry secret.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool IsIngestionToken(string token)
        => token.Length is >= 12 and <= 128 && token.StartsWith("o2oi_", StringComparison.Ordinal)
            && token.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string EncodeBasic(string name, string password)
        => Convert.ToBase64String(Encoding.ASCII.GetBytes($"{name}:{password}"));
}
