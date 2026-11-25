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
    public const string HelperRunLockPath = "/run/kautuka-helper-run.lock";
    private const int LockShared = 1;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private const int TryAgain = 11;
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

    public static bool TryAcquireHelperRun(out KautukaDatabaseWriterLease? lease)
    {
        lease = null;
        if (!OperatingSystem.IsLinux() || !string.Equals(Environment.GetEnvironmentVariable("Kautuka__Appliance__Enabled"), "true", StringComparison.OrdinalIgnoreCase))
            return true;
        SafeFileHandle handle = File.OpenHandle(HelperRunLockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            if (flock(handle.DangerousGetHandle().ToInt32(), LockShared | LockNonBlocking) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != TryAgain) throw new Win32Exception(error, "Could not acquire the Kautuka helper run lease.");
                handle.Dispose();
                return false;
            }
            Span<byte> flag = stackalloc byte[1];
            if (RandomAccess.Read(handle, flag, 0) != 0 && flag[0] != (byte)'0')
            {
                _ = flock(handle.DangerousGetHandle().ToInt32(), LockUnlock);
                handle.Dispose();
                return false;
            }
            lease = new KautukaDatabaseWriterLease(handle);
            return true;
        }
        catch { handle.Dispose(); throw; }
    }

    public static string? ReadHelperRunMaintenanceMessage()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(HelperRunLockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> flag = stackalloc byte[1];
            if (RandomAccess.Read(handle, flag, 0) != 1) return null;
            return flag[0] switch
            {
                (byte)'1' => "Kautuka is waiting for the active helper to finish before renewing its channel connection.",
                (byte)'2' => "Kautuka couldn’t renew its channel connection. New messages are queued while Kautuka retries.",
                _ => null,
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Never block a Gateway startup or shutdown thread while the host is restarting it.
    public static async ValueTask<KautukaDatabaseWriterLease?> AcquireHelperRunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAcquireHelperRun(out KautukaDatabaseWriterLease? lease)) return lease;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static KautukaDatabaseWriterLease? Acquire(bool exclusive, string lockPath = LockPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        SafeFileHandle handle = File.OpenHandle(
            lockPath,
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
