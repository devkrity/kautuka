using System.Text;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed class DnsTransactionStore(string directory)
{
    private readonly string path = Path.Combine(directory, "dns-transaction.json");

    internal DnsTransactionState? Read()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("DNS transaction state is too large.");
        return DnsTransactionState.Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    internal void Write(DnsTransactionState state)
    {
        string temporary = Path.Combine(directory, ".dns-transaction-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                byte[] bytes = Encoding.UTF8.GetBytes(state.Serialize());
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            NetworkAccessPolicyStore.FlushDirectory(directory);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
