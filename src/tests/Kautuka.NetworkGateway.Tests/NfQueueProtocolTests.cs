using System.Buffers.Binary;
using System.Net;

using Kautuka.NetworkGateway.Packets;
using Kautuka.NetworkGateway.Admission;

namespace Kautuka.NetworkGateway.Tests;

public sealed class NfQueueProtocolTests
{
    [Fact]
    public void KernelPacketCarriesTupleConntrackGenerationAndIngressIdentity()
    {
        byte[] message = Packet();
        QueuedPacket packet = NfQueueProtocol.Decode(message, 23199);
        Assert.Equal(17u, packet.PacketId);
        Assert.NotNull(packet.Flow);
        Assert.Equal(42u, packet.Flow.ConntrackId);
        Assert.Equal(11u, packet.Flow.IngressInterfaceIndex);
        Assert.Equal(IPAddress.Parse("10.231.251.2"), packet.Flow.Source);
        Assert.Equal((ushort)20199, packet.Flow.DestinationPort);
    }

    [Fact]
    public void MissingConntrackCannotBecomeAnAuthorisedFlow()
    {
        byte[] message = Packet(includeConntrack: false);
        QueuedPacket packet = NfQueueProtocol.Decode(message, 23199);
        Assert.Equal(17u, packet.PacketId);
        Assert.Null(packet.Flow);
    }

    [Fact]
    public void EveryTruncatedEnvelopeIsRejected()
    {
        byte[] message = Packet();
        for (int length = 0; length < message.Length; length++)
        {
            int prefixLength = length;
            Assert.Throws<InvalidDataException>(() => NfQueueProtocol.Decode(message.AsSpan(0, prefixLength), 23199));
        }
    }

    [Fact]
    public void QueueMismatchAndDuplicateIdentityAttributesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => NfQueueProtocol.Decode(Packet(), 1));
        Assert.Throws<InvalidDataException>(() => NfQueueProtocol.Decode(Packet(duplicateInput: true), 23199));
    }

    [Fact]
    public void MalformedAttributeLengthsCannotEscapeBounds()
    {
        byte[] message = Packet();
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(20), ushort.MaxValue);
        Assert.Throws<InvalidDataException>(() => NfQueueProtocol.Decode(message, 23199));
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(20), 3);
        Assert.Throws<InvalidDataException>(() => NfQueueProtocol.Decode(message, 23199));
    }

    [Fact]
    public void DropVerdictNeverEncodesAccept()
    {
        byte[] drop = NfQueueProtocol.Verdict(23199, 17, 0);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(drop.AsSpan(24)));
        Assert.Equal(17u, BinaryPrimitives.ReadUInt32BigEndian(drop.AsSpan(28)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(drop.AsSpan(36)));
        byte[] allow = NfQueueProtocol.Verdict(23199, 17, 123);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(allow.AsSpan(24)));
        Assert.Equal(123u, BinaryPrimitives.ReadUInt32BigEndian(allow.AsSpan(36)));
    }

    [Fact]
    public void ConfigurationExplicitlyDisablesFailOpenAndRequestsConntrack()
    {
        byte[] config = NfQueueProtocol.Configure(23199, 2, bind: false);
        // UAPI: packed copy params attr is 12 bytes aligned; maxlen, mask and flags follow.
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32BigEndian(config.AsSpan(44)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32BigEndian(config.AsSpan(52)));
    }

    [Fact]
    public void LocalOutputUsesKernelHookSocketCredentialsAndEgressIdentity()
    {
        QueuedPacket packet = NfQueueProtocol.Decode(Packet(hook: 3, uid: 215, egress: 12), 23199, namespaceGeneration: 9);
        Assert.NotNull(packet.Flow);
        Assert.Equal(AdmissionPath.LocalOutput, packet.Flow.Path);
        Assert.Equal(215u, packet.Flow.SocketUid);
        Assert.Equal(12u, packet.Flow.EgressInterfaceIndex);
        Assert.Equal(0u, packet.Flow.IngressInterfaceIndex);
        Assert.Equal(9, packet.Flow.NamespaceGeneration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void UnsupportedHooksCannotEnterAdmission(byte hook)
        => Assert.Null(NfQueueProtocol.Decode(Packet(hook: hook, uid: 215), 23199).Flow);

    [Fact]
    public void MissingOutputSocketUidIsDeniedRatherThanBecomingUidZero()
        => Assert.Null(NfQueueProtocol.Decode(Packet(hook: 3, egress: 12), 23199).Flow);

    private static byte[] Packet(bool includeConntrack = true, bool duplicateInput = false, byte hook = 2, uint? uid = null, uint egress = 0)
    {
        byte[] udp = new byte[28];
        udp[0] = 0x45;
        udp[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), 28);
        IPAddress.Parse("10.231.251.2").GetAddressBytes().CopyTo(udp, 12);
        IPAddress.Parse("10.231.252.2").GetAddressBytes().CopyTo(udp, 16);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(20), 41000);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(22), 20199);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(24), 8);
        byte[] header = new byte[7];
        BinaryPrimitives.WriteUInt32BigEndian(header, 17);
        header[4] = 8;
        header[6] = hook;
        var attributes = new List<byte[]> { Attribute(1, header), Attribute(10, udp) };
        if (hook == 2)
        {
            attributes.Add(Attribute(5, NetworkInt(11)));
        }
        if (uid.HasValue)
        {
            attributes.Add(Attribute(16, NetworkInt(uid.Value)));
        }
        if (egress != 0)
        {
            attributes.Add(Attribute(6, NetworkInt(egress)));
        }
        if (includeConntrack)
        {
            attributes.Add(Attribute(0x800B, Attribute(12, NetworkInt(42))));
        }
        if (duplicateInput)
        {
            attributes.Add(Attribute(5, NetworkInt(12)));
        }
        byte[] message = new byte[20 + attributes.Sum(attribute => attribute.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)message.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(4), 0x300);
        message[16] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(18), 23199);
        int offset = 20;
        foreach (byte[] attribute in attributes)
        {
            attribute.CopyTo(message, offset);
            offset += attribute.Length;
        }
        return message;
    }

    private static byte[] Attribute(ushort type, byte[] value)
    {
        byte[] result = new byte[(value.Length + 7) & ~3];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)(value.Length + 4));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), type);
        value.CopyTo(result, 4);
        return result;
    }

    private static byte[] NetworkInt(uint value)
    {
        byte[] result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, value);
        return result;
    }
}
