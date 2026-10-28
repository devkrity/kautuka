namespace Kautuka.CredentialsHost.Security;

internal sealed class CredentialStorageLayout
{
    public CredentialStorageLayout(string serviceRoot)
    {
        ServiceRoot = Path.GetFullPath(serviceRoot);
        VaultRoot = Path.Combine(ServiceRoot, "vault");
        TransportRoot = Path.Combine(ServiceRoot, "transport");
        Directory.CreateDirectory(VaultRoot);
        Directory.CreateDirectory(TransportRoot);
        SecureTransportDirectory(ServiceRoot);
        SecureDirectory(VaultRoot);
        SecureTransportDirectory(TransportRoot);
    }

    public string ServiceRoot { get; }
    public string VaultRoot { get; }
    public string TransportRoot { get; }

    public static void PublishGatewayReadableFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
    }

    public void ValidateExternalRecoveryKeyPath(string recoveryKeyPath)
    {
        string canonical = Path.GetFullPath(recoveryKeyPath);
        string relative = Path.GetRelativePath(ServiceRoot, canonical);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("The recovery protection key must be owned outside the CredentialsHost service root.");
        }
    }

    private static void SecureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            OwnerOnlyFile.RestrictDirectory(path);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SecureTransportDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
        }
    }
}
