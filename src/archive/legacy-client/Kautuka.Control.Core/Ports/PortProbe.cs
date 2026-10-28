using System.Net.Sockets;

using Kautuka.Control.Core.Config;

namespace Kautuka.Control.Core.Ports;

public static class PortProbe
{
    private sealed record DefaultPort(string Key, ushort Value);

    private static readonly DefaultPort[] defaults =
    [
        new("gateway_public", 8000),
        new("ui_host",        8001),
        new("channelhost",    8098),
        new("workspace_grpc", 8100),
    ];

    /// <summary>
    /// Returns the first free port at or above each default, avoiding collisions
    /// between assignments.
    /// </summary>
    public static PortAssignments ProbeAll()
    {
        var used = new List<ushort>();

        ushort Pick(ushort start)
        {
            var candidate = start;
            while (true)
            {
                if (!used.Contains(candidate) && IsFree(candidate))
                {
                    used.Add(candidate);
                    return candidate;
                }
                candidate = (ushort)(candidate + 1);
            }
        }

        return new PortAssignments
        {
            GatewayPublic = Pick(defaults[0].Value),
            UiHost = Pick(defaults[1].Value),
            ChannelHost = Pick(defaults[2].Value),
            WorkspaceGrpc = Pick(defaults[3].Value),
        };
    }

    public static bool IsFree(ushort port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
