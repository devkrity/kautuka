using System.Runtime.InteropServices;

namespace Kautuka.OciHook;

internal static partial class HookSignals
{
    // Linux x64 and arm64. Leave every other signal in the runtime's inherited mask.
    private const int ChildSignal = 17;
    private const int Unblock = 1;

    internal static void EnableChildExitNotification()
    {
        // crun blocks SIGCHLD before exec. .NET Process waits need it delivered.
        if (EmptySet(out SignalSet mask) != 0 || AddSignal(ref mask, ChildSignal) != 0
            || SetThreadMask(Unblock, in mask, IntPtr.Zero) != 0)
        {
            throw new InvalidOperationException("The OCI hook could not enable child exit notifications.");
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct SignalSet { }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "sigemptyset")]
    private static partial int EmptySet(out SignalSet set);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "sigaddset")]
    private static partial int AddSignal(ref SignalSet set, int signal);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "pthread_sigmask")]
    private static partial int SetThreadMask(int action, in SignalSet set, IntPtr previous);
}
