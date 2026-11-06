using System.Buffers.Binary;
using System.Net;

using Kautuka.NetworkGateway.Packets;

namespace Kautuka.NetworkGateway.Tests;

public sealed class IpPacketDecoderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteUdpTupleIsDecodedForBothFamilies(bool ipv6)
    {
        byte[] packet = Udp(ipv6);
        Assert.True(IpPacketDecoder.TryDecode(packet, 42, 3, out var flow));
        Assert.Equal(42u, flow!.ConntrackId);
        Assert.Equal((ushort)3, flow.Zone);
        Assert.Equal((ushort)41000, flow.SourcePort);
        Assert.Equal((ushort)443, flow.DestinationPort);
        Assert.Equal((byte)17, flow.Protocol);
    }

    [Fact]
    public void EveryTruncatedPrefixIsRejectedWithoutThrowing()
    {
        foreach (bool ipv6 in new[] { false, true })
        {
            byte[] packet = Udp(ipv6);
            for (int length = 0; length < packet.Length; length++)
            {
                Assert.False(IpPacketDecoder.TryDecode(packet.AsSpan(0, length), 42, 0, out _));
            }
        }
    }

    [Theory]
    [InlineData(0x2000)]
    [InlineData(0x0001)]
    public void FragmentCannotSupplyAnUnauthoritativeTransportTuple(ushort fragment)
    {
        byte[] packet = Udp(false);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), fragment);
        Assert.False(IpPacketDecoder.TryDecode(packet, 42, 0, out _));
    }

    [Fact]
    public void UnknownConntrackGenerationAndMalformedUdpLengthAreRejected()
    {
        byte[] packet = Udp(false);
        Assert.False(IpPacketDecoder.TryDecode(packet, 0, 0, out _));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), 9);
        Assert.False(IpPacketDecoder.TryDecode(packet, 42, 0, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(43)]
    [InlineData(44)]
    [InlineData(50)]
    [InlineData(60)]
    public void UnsupportedIpv6ExtensionsFailClosed(byte nextHeader)
    {
        byte[] packet = Udp(true);
        packet[6] = nextHeader;
        Assert.False(IpPacketDecoder.TryDecode(packet, 42, 0, out _));
    }

    [Fact]
    public void Ipv4MappedIpv6CannotAliasAnotherPolicyIdentity()
    {
        byte[] packet = Udp(true);
        IPAddress.Parse("::ffff:10.0.0.1").GetAddressBytes().CopyTo(packet, 8);
        Assert.False(IpPacketDecoder.TryDecode(packet, 42, 0, out _));
    }

    private static byte[] Udp(bool ipv6)
    {
        int header = ipv6 ? 40 : 20;
        byte[] packet = new byte[header + 8];
        if (ipv6)
        {
            packet[0] = 0x60;
            packet[6] = 17;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 8);
            IPAddress.Parse("fd42::1").GetAddressBytes().CopyTo(packet, 8);
            IPAddress.Parse("fd42::2").GetAddressBytes().CopyTo(packet, 24);
        }
        else
        {
            packet[0] = 0x45;
            packet[9] = 17;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
            IPAddress.Parse("10.0.0.1").GetAddressBytes().CopyTo(packet, 12);
            IPAddress.Parse("10.0.0.2").GetAddressBytes().CopyTo(packet, 16);
        }
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(header), 41000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(header + 2), 443);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(header + 4), 8);
        return packet;
    }
}
