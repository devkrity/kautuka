using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Security;
using Kautuka.Telemetry.Telemetry;

namespace Kautuka.Manager.Hosting;

internal static class ApplianceTelemetry
{
    internal const string StateName = "observability";
    internal const string CollectorConfigPath = "/run/kautuka-observability.env";
    private const string IngestionSecretName = "kautuka-appliance-otlp";
    private const string IngestionEndpoint = "http://127.0.0.1:5080/api/default/ingestion-tokens";
    private const string PasswordSetting = "ZO_ROOT_USER_PASSWORD=";

    internal static string RootConfigurationPath(string stateRoot)
        => Path.Combine(SecretDirectory(stateRoot), "collector.env");

    private static string SecretDirectory(string stateRoot) => Path.Combine(stateRoot, "manager", "telemetry");
    private static string IngestionSecretPath(string stateRoot) => Path.Combine(SecretDirectory(stateRoot), "ingestion");

    internal static void EnsureRootConfiguration(string stateRoot)
    {
        EnsurePrivateDirectory(SecretDirectory(stateRoot));
        string path = RootConfigurationPath(stateRoot);
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidOperationException("The appliance observability configuration must be a regular file.");
        if (File.Exists(path))
        {
            _ = ReadRootAuthorization(stateRoot);
            return;
        }

        string data = Path.Combine(stateRoot, StateName);
        if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any())
            throw new InvalidOperationException("The appliance observability secret is missing while its data directory is populated.");

        string password = "A!9" + RandomNumberGenerator.GetHexString(48, lowercase: true);
        WriteSecret(path, $"{PasswordSetting}{password}\n", replace: false, mountedInCollector: true);
    }

    internal static string? ReadIngestionAuthorization(string stateRoot)
    {
        string path = IngestionSecretPath(stateRoot);
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidOperationException("The appliance ingestion secret must be a regular file.");
        if (!File.Exists(path)) return null;
        string retained = File.ReadAllText(path);
        return IsIngestionToken(retained) ? EncodeBasic("default", retained) : null;
    }

    internal static async Task<string?> ProvisionIngestionAuthorizationAsync(PodmanCommandRunner podman, string stateRoot, string collectorId, CancellationToken token)
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
        string rootAuthorization = ReadRootAuthorization(stateRoot);
        (bool Exists, string? Token)? result = ParseIngestionToken(await SendAsync(create: false).ConfigureAwait(false));
        if (result is { Exists: false })
        {
            await SendAsync(create: true).ConfigureAwait(false);
            result = ParseIngestionToken(await SendAsync(create: false).ConfigureAwait(false));
        }
        if (result?.Token is not { } ingestionToken || !IsIngestionToken(ingestionToken)) return null;

        token.ThrowIfCancellationRequested();
        WriteSecret(IngestionSecretPath(stateRoot), ingestionToken, replace: true);
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

    internal static string ReadRootAuthorization(string stateRoot)
    {
        string configuration = File.ReadAllText(RootConfigurationPath(stateRoot));
        if (!configuration.StartsWith(PasswordSetting, StringComparison.Ordinal) || !configuration.EndsWith('\n'))
            throw new InvalidOperationException("The appliance observability configuration has an invalid format.");
        string password = configuration[PasswordSetting.Length..^1];
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

    private static void WriteSecret(string path, string value, bool replace, bool mountedInCollector = false)
    {
        string directory = Path.GetDirectoryName(path)!;
        string temporaryPath = Path.Combine(directory, $".telemetry-{Guid.NewGuid():N}.tmp");
        try
        {
            OwnerOnlySecretFile.WriteAllText(temporaryPath, value);
            // The rootless collector runs as a remapped non-root UID. Its single-file
            // mount can read this file while the private parent protects it on the host.
            if (OperatingSystem.IsLinux() && mountedInCollector)
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temporaryPath, path, overwrite: replace);
        }
        finally { File.Delete(temporaryPath); }
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        if (new DirectoryInfo(directory).LinkTarget is not null)
            throw new InvalidOperationException("The appliance telemetry directory must not be a symbolic link.");
        if (OperatingSystem.IsLinux())
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, ownerOnly);
            File.SetUnixFileMode(directory, ownerOnly);
        }
        else Directory.CreateDirectory(directory);
    }

    private static bool IsIngestionToken(string token)
        => token.Length is >= 12 and <= 128 && token.StartsWith("o2oi_", StringComparison.Ordinal)
            && token.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string EncodeBasic(string name, string password)
        => Convert.ToBase64String(Encoding.ASCII.GetBytes($"{name}:{password}"));
}
