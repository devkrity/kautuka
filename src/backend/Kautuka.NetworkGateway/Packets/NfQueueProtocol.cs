using System.Buffers.Binary;

using Kautuka.NetworkGateway.Admission;

namespace Kautuka.NetworkGateway.Packets;

/// <summary>Linux nfnetlink_queue UAPI codec. Headers are native endian; payload fields use network order.</summary>
internal static class NfQueueProtocol
{
    public static byte[] Configure(ushort queue, uint sequence, bool bind)
    {
        if (bind)
        {
            // NFQA_CFG_CMD: BIND, padding, AF_UNSPEC. No global PF unbind operation.
            return Message(0x302, queue, sequence, 5, [(1, [1, 0, 0, 0])]);
        }

        byte[] copy = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(copy, 65535);
        copy[4] = 2; // NFQNL_COPY_PACKET
        return Message(0x302, queue, sequence, 5,
            [(2, copy), (3, NetworkUInt32(4096)), (4, NetworkUInt32(11)), (5, NetworkUInt32(10))]);
        // Mask FAIL_OPEN|CONNTRACK|UID_GID; enable CONNTRACK and socket credentials, disable FAIL_OPEN.
    }

    public static byte[] Verdict(ushort queue, uint packetId, uint handle)
    {
        byte[] verdict = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(verdict, handle == 0 ? 0u : 1u); // NF_DROP / NF_ACCEPT
        BinaryPrimitives.WriteUInt32BigEndian(verdict.AsSpan(4), packetId);
        return Message(0x301, queue, 0, 1, [(2, verdict), (3, NetworkUInt32(handle))]);
    }

    public static QueuedPacket Decode(ReadOnlySpan<byte> message, ushort queue, long namespaceGeneration = 1)
    {
        RequireLittleEndian();
        if (message.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(message) != message.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(message[4..]) != 0x300
            || BinaryPrimitives.ReadUInt16BigEndian(message[18..]) != queue)
        {
            throw new InvalidDataException("Invalid NFQUEUE packet envelope.");
        }

        Dictionary<ushort, byte[]> attributes = Attributes(message[20..], allowTerminalPayload: true);
        if (!attributes.TryGetValue(1, out byte[]? header) || header.Length != 7)
        {
            throw new InvalidDataException("Missing NFQUEUE packet identifier.");
        }

        uint packetId = BinaryPrimitives.ReadUInt32BigEndian(header);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(namespaceGeneration);
        uint ingress = attributes.TryGetValue(5, out byte[]? input) ? ReadUInt32(input) : 0;
        uint egress = attributes.TryGetValue(6, out byte[]? output) ? ReadUInt32(output) : 0;
        uint? socketUid = attributes.TryGetValue(16, out byte[]? uid) ? ReadUInt32(uid) : null;
        byte hook = header[6];
        uint originalMark = attributes.TryGetValue(3, out byte[]? mark) ? ReadUInt32(mark) : 0;
        uint ctId = 0;
        ushort zone = 0;
        if (attributes.TryGetValue(11, out byte[]? ct))
        {
            Dictionary<ushort, byte[]> conntrack = Attributes(ct);
            if (conntrack.TryGetValue(12, out byte[]? id))
            {
                ctId = ReadUInt32(id);
            }
            if (conntrack.TryGetValue(18, out byte[]? rawZone))
            {
                if (rawZone.Length != 2)
                {
                    throw new InvalidDataException("Invalid conntrack zone.");
                }
                zone = BinaryPrimitives.ReadUInt16BigEndian(rawZone);
            }
        }

        FlowKey? flow = null;
        if (attributes.TryGetValue(10, out byte[]? payload)
            && hook is (byte)AdmissionPath.Forwarded or (byte)AdmissionPath.LocalOutput
            && (hook != (byte)AdmissionPath.LocalOutput || socketUid.HasValue)
            && IpPacketDecoder.TryDecode(payload, ctId, zone, out FlowKey? decoded))
        {
            flow = decoded! with
            {
                IngressInterfaceIndex = ingress,
                EgressInterfaceIndex = egress,
                Path = (AdmissionPath)hook,
                SocketUid = hook == (byte)AdmissionPath.LocalOutput ? socketUid : null,
                NamespaceGeneration = namespaceGeneration,
            };
        }

        return new QueuedPacket(packetId, originalMark, flow);
    }

    public static int Acknowledgement(ReadOnlySpan<byte> message, uint sequence)
    {
        RequireLittleEndian();
        if (message.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(message) != message.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(message[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(message[8..]) != sequence)
        {
            throw new InvalidDataException("Invalid netlink configuration acknowledgement.");
        }
        return BinaryPrimitives.ReadInt32LittleEndian(message[16..]);
    }

    private static Dictionary<ushort, byte[]> Attributes(ReadOnlySpan<byte> data, bool allowTerminalPayload = false)
    {
        var attributes = new Dictionary<ushort, byte[]>();
        while (!data.IsEmpty)
        {
            if (data.Length < 4)
            {
                throw new InvalidDataException("Truncated netlink attribute header.");
            }
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data);
            ushort type = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) & 0x3FFF);
            int aligned = (length + 3) & ~3;
            // nfqnl_build_packet_message appends the zero-copy NFQA_PAYLOAD last,
            // without tail alignment. All other/nested attributes retain strict alignment.
            bool terminalPayload = allowTerminalPayload && type == 10 && length == data.Length;
            if (length < 4 || length > data.Length || (aligned > data.Length && !terminalPayload))
            {
                throw new InvalidDataException($"Truncated netlink attribute: type={type}, length={length}, remaining={data.Length}.");
            }
            if (!attributes.TryAdd(type, data.Slice(4, length - 4).ToArray()))
            {
                throw new InvalidDataException($"Duplicate netlink attribute: type={type}, length={length}.");
            }
            data = terminalPayload ? [] : data[aligned..];
        }
        return attributes;
    }

    private static uint ReadUInt32(byte[] data)
        => data.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : throw new InvalidDataException("Invalid netlink integer attribute.");

    private static byte[] NetworkUInt32(uint value)
    {
        byte[] result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, value);
        return result;
    }

    private static byte[] Message(ushort type, ushort queue, uint sequence, ushort flags, (ushort Type, byte[] Value)[] attributes)
    {
        RequireLittleEndian();
        int length = 20 + attributes.Sum(attribute => (attribute.Value.Length + 7) & ~3);
        byte[] result = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), type);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(18), queue);
        int offset = 20;
        foreach ((ushort attributeType, byte[] value) in attributes)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset), (ushort)(value.Length + 4));
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset + 2), attributeType);
            value.CopyTo(result, offset + 4);
            offset += (value.Length + 7) & ~3;
        }
        return result;
    }

    private static void RequireLittleEndian()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The prototype supports little-endian Linux targets only.");
        }
    }
}

internal sealed record QueuedPacket(uint PacketId, uint OriginalMark, FlowKey? Flow);
