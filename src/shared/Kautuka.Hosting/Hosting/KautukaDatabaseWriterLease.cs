using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.Hosting.Hosting;

/// <summary>
/// Shared process lease held by database writer hosts. The privileged update
/// helper takes the matching exclusive flock after setting durable maintenance
/// and draining host units, preventing a supported host from starting inside a
/// snapshot or restore window.
/// </summary>
public sealed class KautukaDatabaseWriterLease : IDisposable
{
    public const string LockPath = "/run/kautuka-db-writer.lock";
    private const int LockShared = 1;
    private const int LockExclusive = 2;
    private const int LockUnlock = 8;
    private readonly SafeFileHandle handle;
    private bool disposed;

    private KautukaDatabaseWriterLease(SafeFileHandle handle) => this.handle = handle;

    public static KautukaDatabaseWriterLease? Acquire() => Acquire(exclusive: false);

    public static KautukaDatabaseWriterLease? AcquireExclusive() => Acquire(exclusive: true);

    public static KautukaDatabaseWriterLease? AcquireForMode(string? mode, bool exclusive = false)
    {
        if (!string.Equals(mode, "prod", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Acquire(exclusive);
    }

    private static KautukaDatabaseWriterLease? Acquire(bool exclusive)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        SafeFileHandle handle = File.OpenHandle(
            LockPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        try
        {
            if (flock(handle.DangerousGetHandle().ToInt32(), exclusive ? LockExclusive : LockShared) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not acquire the Kautuka database writer lease.");
            }

            return new KautukaDatabaseWriterLease(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        _ = flock(handle.DangerousGetHandle().ToInt32(), LockUnlock);
        handle.Dispose();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);
}
