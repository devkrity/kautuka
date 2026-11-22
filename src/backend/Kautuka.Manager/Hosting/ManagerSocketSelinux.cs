using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace Kautuka.Manager.Hosting;

internal static class ManagerSocketSelinux
{
    internal const string ClientType = "kautuka_manager_client.process";
    private const string ListenerContext = "system_u:object_r:kautuka_manager_rpc_socket_t:s0";

    internal static Socket CreateBoundSocket(EndPoint endpoint, string managerSocketPath)
    {
        if (endpoint is not UnixDomainSocketEndPoint || endpoint.ToString() != managerSocketPath)
            return SocketTransportOptions.CreateDefaultBoundListenSocket(endpoint);

        Check(GetSocketCreateContext(out nint previous), "read the current context");
        Socket? socket = null;
        try
        {
            nint label = Marshal.StringToCoTaskMemUTF8(ListenerContext);
            try
            {
                Check(SetSocketCreateContext(label), "set the Manager RPC context");
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            }
            finally
            {
                Marshal.FreeCoTaskMem(label);
            }
        }
        finally
        {
            // SELinux socket creation context is thread-local. Restore it before binding.
            RestoreContext(previous, socket);
        }

        try
        {
            socket.Bind(endpoint);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void RestoreContext(nint previous, Socket? socket)
    {
        try
        {
            Check(SetSocketCreateContext(previous), "restore the previous context");
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
        finally
        {
            FreeContext(previous);
        }
    }

    private static void Check(int result, string operation)
    {
        if (result != 0)
            throw new InvalidOperationException($"SELinux could not {operation}.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [DllImport("libselinux.so.1", EntryPoint = "getsockcreatecon", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetSocketCreateContext(out nint context);

    [DllImport("libselinux.so.1", EntryPoint = "setsockcreatecon", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SetSocketCreateContext(nint context);

    [DllImport("libselinux.so.1", EntryPoint = "freecon")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void FreeContext(nint context);
}
