using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

using Kautuka.Security;

namespace Kautuka.Gateway.Domain.Auth;

internal sealed class SetupPossession(KautukaDatabase database)
{
    private readonly object gate = new();
    private readonly string path = Path.Combine(database.DataDirectory, "setup-token");

    public string TokenPath => path;

    public void EnsureIssued()
    {
        lock (gate)
        {
            if (File.Exists(path))
            {
                RestrictToOwner(path);
                return;
            }

            Directory.CreateDirectory(database.DataDirectory);
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            OwnerOnlySecretFile.WriteAllText(path, token);
            RestrictToOwner(path);
        }
    }

    internal static void RestrictToOwner(string tokenPath)
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
            new FileInfo(tokenPath).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public string Peek()
    {
        EnsureIssued();
        return File.ReadAllText(path).Trim();
    }

    public bool TryRedeem(string? presented)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(presented) || !File.Exists(path))
            {
                return false;
            }

            byte[] expected = Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
            byte[] actual = Encoding.UTF8.GetBytes(presented.Trim());
            if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    public bool IsValid(string? presented)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(presented) || !File.Exists(path)) return false;
            byte[] expected = Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
            byte[] actual = Encoding.UTF8.GetBytes(presented.Trim());
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }
}
