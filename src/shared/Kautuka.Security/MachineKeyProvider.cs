using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Kautuka.Security;

/// <summary>
/// Manages the 32-byte machine key used by AES-256-GCM secret encryption.
/// Key is stored as a raw binary file at data/kautuka.key — separate from the
/// SQLite database so that a DB copy alone cannot decrypt secrets.
/// Auto-generated on first startup; persists across restarts.
/// Cross-platform: no DPAPI or OS-specific key stores.
/// Native AOT safe: no reflection.
/// </summary>
public sealed class MachineKeyProvider
{
    private const int keySizeBytes = 32;
    private const string keyFileName = "kautuka.key";
    private readonly byte[] key;

    public MachineKeyProvider(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        string keyPath = ResolveKeyPath(dataRoot);
        key = LoadOrCreate(keyPath);
    }

    /// <summary>Returns the 32-byte machine key. Callers must not modify the returned array.</summary>
    public ReadOnlyMemory<byte> Key => key;

    private static byte[] LoadOrCreate(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            RestrictToOwner(keyPath);
            byte[] existing = File.ReadAllBytes(keyPath);
            if (existing.Length != keySizeBytes)
            {
                throw new InvalidOperationException(
                    $"Machine key file at '{keyPath}' is corrupt (expected {keySizeBytes} bytes, got {existing.Length}). " +
                    "Delete the file and restart to generate a new key — all encrypted secrets will need to be re-entered.");
            }

            return existing;
        }

        string? dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        byte[] key = RandomNumberGenerator.GetBytes(keySizeBytes);
        OwnerOnlySecretFile.WriteAllBytes(keyPath, key);
        RestrictToOwner(keyPath);

        return key;
    }

    private static void RestrictToOwner(string keyPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Read | FileSystemRights.Write,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            new FileInfo(keyPath).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string ResolveKeyPath(string dataRoot)
        => Path.GetFullPath(Path.Combine(dataRoot, keyFileName));
}
