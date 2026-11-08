using System.Security.Cryptography;
using System.Text;

using Kautuka.Manager.RuntimeContainer.Sandbox;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>Stores the Manager-only OpenObserve root credential and the restricted exporter credential.</summary>
public sealed class OpenObserveCredentialStore(string sharedDataRoot)
{
    public const string RootUserEmail = "kautuka-telemetry@kautuka.local";
    public const string IngestionTokenName = "kautuka-managed-telemetry";

    private const UnixFileMode privateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode privateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const string lower = "abcdefghijklmnopqrstuvwxyz";
    private const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string digits = "0123456789";
    private const string special = "!@#$%^&*()-_=+[]{}?";
    private const string passwordFileName = "openobserve-root-password";
    private const string authorizationFileName = "ingestion.authorization";

    private string TelemetryDirectoryPath => Path.Combine(Path.GetFullPath(sharedDataRoot), "telemetry");
    private string RootCredentialDirectoryPath => Path.Combine(TelemetryDirectoryPath, "manager");
    public string ExporterDirectoryPath => Path.Combine(TelemetryDirectoryPath, "exporter");
    public string RootPasswordPath => Path.Combine(RootCredentialDirectoryPath, passwordFileName);
    public string IngestionAuthorizationPath => Path.Combine(ExporterDirectoryPath, authorizationFileName);

    public string GetOrCreateRootPassword()
    {
        EnsurePrivateDirectory(RootCredentialDirectoryPath);
        if (File.Exists(RootPasswordPath))
            return ReadExistingRootPassword();

        string generated = GeneratePassword();
        string temporary = Path.Combine(RootCredentialDirectoryPath, ".openobserve-root-password-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteNewPrivateFile(temporary, generated);
            try
            {
                File.Move(temporary, RootPasswordPath);
            }
            catch (IOException) when (File.Exists(RootPasswordPath))
            {
                return ReadExistingRootPassword();
            }
            NetworkAccessPolicyStore.FlushDirectory(RootCredentialDirectoryPath);
            return generated;
        }
        finally
        {
            if (File.Exists(temporary)) PodmanContainerTool.DeleteSecretFile(temporary);
        }
    }

    public void SaveIngestionAuthorizationHeader(string header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        if (!header.StartsWith("Basic ", StringComparison.Ordinal)
            || header.Any(static character => character is '\r' or '\n' or '\0'))
            throw new ArgumentException("OpenObserve ingestion authorization must be a single Basic header value.", nameof(header));

        EnsurePrivateDirectory(ExporterDirectoryPath);
        string temporary = Path.Combine(ExporterDirectoryPath, ".ingestion-authorization-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteNewPrivateFile(temporary, header);
            File.Move(temporary, IngestionAuthorizationPath, overwrite: true);
            SetPrivateFileMode(IngestionAuthorizationPath);
            NetworkAccessPolicyStore.FlushDirectory(ExporterDirectoryPath);
        }
        finally
        {
            if (File.Exists(temporary)) PodmanContainerTool.DeleteSecretFile(temporary);
        }
    }

    public void ClearIngestionAuthorizationHeader()
    {
        if (!File.Exists(IngestionAuthorizationPath)) return;
        SetPrivateFileMode(IngestionAuthorizationPath);
        PodmanContainerTool.DeleteSecretFile(IngestionAuthorizationPath);
        NetworkAccessPolicyStore.FlushDirectory(ExporterDirectoryPath);
    }

    public void EnsureExporterDirectory() => EnsurePrivateDirectory(ExporterDirectoryPath);

    public string? ReadIngestionAuthorizationHeader()
    {
        if (!File.Exists(IngestionAuthorizationPath)) return null;
        SetPrivateFileMode(IngestionAuthorizationPath);
        string header = File.ReadAllText(IngestionAuthorizationPath, Encoding.UTF8).Trim();
        if (header.Length == 0) return null;
        if (!header.StartsWith("Basic ", StringComparison.Ordinal)
            || header.Any(static character => character is '\r' or '\n' or '\0'))
            throw new InvalidOperationException("The persisted OpenObserve ingestion credential is invalid; repair the credential without deleting collector data.");
        return header;
    }

    public static string CreateBasicAuthorizationHeader(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
    }

    private void EnsurePrivateDirectory(string directory)
    {
        string sharedRoot = Path.GetFullPath(sharedDataRoot);
        bool sharedRootExisted = Directory.Exists(sharedRoot);
        bool telemetryDirectoryExisted = Directory.Exists(TelemetryDirectoryPath);
        Directory.CreateDirectory(TelemetryDirectoryPath);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(TelemetryDirectoryPath, privateDirectoryMode);
        if (!sharedRootExisted) NetworkAccessPolicyStore.FlushDirectory(Path.GetDirectoryName(sharedRoot)!);
        if (!telemetryDirectoryExisted) NetworkAccessPolicyStore.FlushDirectory(sharedRoot);
        bool existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, privateDirectoryMode);
        if (!existed) NetworkAccessPolicyStore.FlushDirectory(TelemetryDirectoryPath);
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, privateFileMode);
    }

    private string ReadExistingRootPassword()
    {
        SetPrivateFileMode(RootPasswordPath);
        string existing = File.ReadAllText(RootPasswordPath, Encoding.UTF8).Trim();
        if (existing.Length is < 8 or > 128)
            throw new InvalidOperationException("The persisted OpenObserve root credential is invalid; preserve collector data and repair the credential file.");
        NetworkAccessPolicyStore.FlushDirectory(RootCredentialDirectoryPath);
        return existing;
    }

    private static void WriteNewPrivateFile(string path, string contents)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = privateFileMode;
        using var stream = new FileStream(path, options);
        byte[] bytes = Encoding.UTF8.GetBytes(contents + "\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string GeneratePassword()
    {
        char[] password = new char[48];
        password[0] = RandomCharacter(lower);
        password[1] = RandomCharacter(upper);
        password[2] = RandomCharacter(digits);
        password[3] = RandomCharacter(special);
        string alphabet = lower + upper + digits + special;
        for (int index = 4; index < password.Length; index++) password[index] = RandomCharacter(alphabet);
        for (int index = password.Length - 1; index > 0; index--)
        {
            int other = RandomNumberGenerator.GetInt32(index + 1);
            (password[index], password[other]) = (password[other], password[index]);
        }
        return new string(password);
    }

    private static char RandomCharacter(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
