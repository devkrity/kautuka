using System.Buffers.Binary;
using System.Net;

using Kautuka.NetworkGateway.Admission;

namespace Kautuka.NetworkGateway.Packets;

/// <summary>
/// Bounded decoder for complete, unfragmented TCP/UDP IP packets in the Gate 1 probe.
/// IPv6 extension headers and fragments fail closed until their semantics are implemented.
/// </summary>
internal static class IpPacketDecoder
{
    public static bool TryDecode(ReadOnlySpan<byte> packet, uint conntrackId, ushort zone, out FlowKey? flow)
    {
        flow = null;
        if (packet.Length < 20 || packet.Length > 65575 || conntrackId == 0)
        {
            return false;
        }

        IPAddress source;
        IPAddress destination;
        int headerLength;
        byte protocol;
        switch (packet[0] >> 4)
        {
            case 4:
                headerLength = (packet[0] & 0x0F) * 4;
                if (headerLength < 20 || headerLength > packet.Length
                    || BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) != packet.Length
                    || (BinaryPrimitives.ReadUInt16BigEndian(packet[6..]) & 0x3FFF) != 0)
                {
                    return false;
                }

                protocol = packet[9];
                source = new IPAddress(packet.Slice(12, 4));
                destination = new IPAddress(packet.Slice(16, 4));
                break;
            case 6:
                headerLength = 40;
                if (packet.Length < headerLength
                    || BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) + headerLength != packet.Length)
                {
                    return false;
                }

                protocol = packet[6];
                source = new IPAddress(packet.Slice(8, 16));
                destination = new IPAddress(packet.Slice(24, 16));
                break;
            default:
                return false;
        }

        ReadOnlySpan<byte> transport = packet[headerLength..];
        if (protocol == 17)
        {
            if (transport.Length < 8 || BinaryPrimitives.ReadUInt16BigEndian(transport[4..]) != transport.Length)
            {
                return false;
            }
        }
        else if (protocol == 6)
        {
            if (transport.Length < 20 || (transport[12] >> 4) * 4 is < 20
                || (transport[12] >> 4) * 4 > transport.Length)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(transport);
        ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(transport[2..]);
        if (sourcePort == 0 || destinationPort == 0 || source.IsIPv4MappedToIPv6 || destination.IsIPv4MappedToIPv6)
        {
            return false;
        }

        flow = new FlowKey(conntrackId, zone, source, sourcePort, destination, destinationPort, protocol);
        return true;
    }
}
