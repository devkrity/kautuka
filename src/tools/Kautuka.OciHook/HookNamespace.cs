using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.OciHook;

/// <summary>Keeps the verified Linux network namespace open for the entire hook operation.</summary>
internal sealed partial class HookNamespace(SafeFileHandle handle, ProcessIdentity.Identity identity) : IDisposable
{
    private static readonly TimeSpan commandTimeout = TimeSpan.FromSeconds(10);
    private const int AtEmptyPath = 0x1000;
    private const uint InodeMask = 0x100;

    public ProcessIdentity.Identity Identity { get; } = identity;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The returned namespace owns the descriptor; failures dispose it before throwing.")]
    public static HookNamespace Open(int pid)
    {
        ProcessIdentity.Identity before = ProcessIdentity.TryRead(pid)
            ?? throw new InvalidOperationException("Could not read container process identity.");
        string pidText = pid.ToString(CultureInfo.InvariantCulture);
        SafeFileHandle descriptor = File.OpenHandle($"/proc/{pidText}/ns/net", FileMode.Open, FileAccess.Read);
        try
        {
            if (Statx(descriptor, string.Empty, AtEmptyPath, InodeMask, out StatxResult opened) != 0
                || (opened.Mask & InodeMask) == 0
                || opened.Inode != before.NetworkNamespaceInode
                || ProcessIdentity.TryRead(pid) != before)
            {
                throw new InvalidOperationException("Container network namespace changed during lookup.");
            }

            return new(descriptor, before);
        }
        catch
        {
            descriptor.Dispose();
            throw;
        }
    }

    public Task<HookProcess.Result> RunAsync(string executable, IReadOnlyList<string> arguments, string? input = null)
    {
        string path = FormattableString.Invariant($"/proc/{Environment.ProcessId}/fd/{handle.DangerousGetHandle()}");
        return HookProcess.RunAsync("/usr/bin/nsenter", [$"--net={path}", "--", executable, .. arguments],
            input, commandTimeout, 1024 * 1024);
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(SafeFileHandle descriptor, string path, int flags, uint mask, out StatxResult result);
}
