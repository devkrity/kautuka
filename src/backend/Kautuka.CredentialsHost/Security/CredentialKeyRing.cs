using System.Security.Cryptography;

namespace Kautuka.CredentialsHost.Security;

internal sealed class CredentialKeyRing : IDisposable
{
    private const int keySize = 32;
    private readonly object sync = new();
    private readonly string directory;
    private readonly string activePath;
    private readonly Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);

    public CredentialKeyRing(string dataRoot)
    {
        directory = Path.Combine(dataRoot, "vault-keys");
        activePath = Path.Combine(directory, "active");
        Directory.CreateDirectory(directory);
        SecureDirectory(directory);
        LoadOrCreate();
    }

    public string ActiveKeyId { get; private set; } = string.Empty;

    public int KeyCount
    {
        get { lock (sync) return keys.Count; }
    }

    public (string KeyId, byte[] Key) GetActiveKey()
    {
        lock (sync) return (ActiveKeyId, keys[ActiveKeyId].ToArray());
    }

    public byte[] GetKey(string keyId)
    {
        lock (sync)
        {
            if (!keys.TryGetValue(keyId, out byte[]? key))
                throw new CredentialIntegrityException("Credential envelope references an unavailable encryption key.");
            return key.ToArray();
        }
    }

    public string Rotate()
    {
        lock (sync)
        {
            string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            byte[] key = RandomNumberGenerator.GetBytes(keySize);
            WriteAtomic(KeyPath(id), key);
            WriteAtomic(activePath, System.Text.Encoding.ASCII.GetBytes(id));
            keys.Add(id, key);
            ActiveKeyId = id;
            return id;
        }
    }

    public void RetireAllExcept(string keyId)
    {
        lock (sync)
        {
            foreach (string retired in keys.Keys.Where(candidate => candidate != keyId).ToArray())
            {
                File.Delete(KeyPath(retired));
                CryptographicOperations.ZeroMemory(keys[retired]);
                keys.Remove(retired);
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
            foreach (byte[] key in keys.Values) CryptographicOperations.ZeroMemory(key);
    }

    private void LoadOrCreate()
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.key"))
        {
            byte[] key = File.ReadAllBytes(path);
            if (key.Length != keySize) throw new InvalidOperationException("Credential key ring contains invalid key material.");
            keys.Add(Path.GetFileNameWithoutExtension(path), key);
            SecureFile(path);
        }

        if (!File.Exists(activePath))
        {
            if (keys.Count != 0) throw new InvalidOperationException("Credential key ring has no active-key marker.");
            Rotate();
            return;
        }

        ActiveKeyId = File.ReadAllText(activePath).Trim();
        if (!keys.ContainsKey(ActiveKeyId)) throw new InvalidOperationException("Credential key ring active key is missing.");
        SecureFile(activePath);
    }

    private string KeyPath(string id) => Path.Combine(directory, id + ".key");

    private static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        OwnerOnlyFile.RestrictFile(temp);
        File.Move(temp, path, overwrite: true);
        OwnerOnlyFile.RestrictFile(path);
    }

    private static void SecureDirectory(string path) => OwnerOnlyFile.RestrictDirectory(path);

    private static void SecureFile(string path) => OwnerOnlyFile.RestrictFile(path);
}
