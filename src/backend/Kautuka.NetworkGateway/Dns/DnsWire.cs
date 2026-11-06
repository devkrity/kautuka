using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Kautuka.NetworkGateway.Dns;

internal sealed record DnsQuestion(ushort Id, string Name, ushort Type, ushort Class);
internal sealed record DnsAnswer(IPAddress Address, uint Ttl);

/// <summary>
/// Bounded fixture subset: one uncompressed LDH-hostname question and one A/AAAA answer.
/// Not a general recursive resolver, CNAME/EDNS/DNSSEC implementation, or authorization policy.
/// </summary>
internal static class DnsWire
{
    public const int MaximumMessageBytes = 1232;

    public static bool TryParseQuery(ReadOnlySpan<byte> message, out DnsQuestion? question)
    {
        question = null;
        if (!Header(message) || Read16(message, 2) != 0x0100 || Read16(message, 4) != 1
            || Read16(message, 6) != 0 || Read16(message, 8) != 0 || Read16(message, 10) != 0)
        {
            return false;
        }
        int offset = 12;
        if (!ReadQuestion(message, ref offset, out DnsQuestion? parsed) || offset != message.Length)
        {
            return false;
        }
        question = parsed;
        return true;
    }

    public static bool TryParseAnswer(ReadOnlySpan<byte> message, DnsQuestion expected, out DnsAnswer? answer)
    {
        ArgumentNullException.ThrowIfNull(expected);
        expected = expected with { Name = CanonicalName(expected.Name) };
        answer = null;
        if (!Header(message) || Read16(message, 0) != expected.Id || (Read16(message, 2) & 0x8100) != 0x8100
            || (Read16(message, 2) & ~0x8580) != 0 || Read16(message, 4) != 1 || Read16(message, 6) != 1
            || Read16(message, 8) != 0 || Read16(message, 10) != 0)
        {
            return false;
        }
        int offset = 12;
        if (!ReadQuestion(message, ref offset, out DnsQuestion? echoed) || echoed != expected)
        {
            return false;
        }
        if (offset + 2 <= message.Length && message[offset] == 0xC0 && message[offset + 1] == 12)
        {
            offset += 2; // Only the already-validated question name; no recursive pointers.
        }
        else if (!ReadName(message, ref offset, out string? name) || name != expected.Name)
        {
            return false;
        }
        if (offset + 10 > message.Length || expected.Class != 1 || expected.Type is not (1 or 28)
            || Read16(message, offset) != expected.Type || Read16(message, offset + 2) != expected.Class)
        {
            return false;
        }
        uint ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(offset + 4)..]);
        int length = Read16(message, offset + 8);
        offset += 10;
        if (length != (expected.Type == 1 ? 4 : 16) || offset + length != message.Length)
        {
            return false;
        }
        var address = new IPAddress(message.Slice(offset, length));
        if (address.IsIPv4MappedToIPv6)
        {
            return false;
        }
        answer = new DnsAnswer(address, Math.Min(ttl, 60));
        return true;
    }

    public static byte[] Query(DnsQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        byte[] encoded = Question(question);
        byte[] message = new byte[12 + encoded.Length];
        Write16(message, 0, question.Id);
        Write16(message, 2, 0x0100);
        Write16(message, 4, 1);
        encoded.CopyTo(message, 12);
        return message;
    }

    public static byte[] Response(DnsQuestion question, DnsAnswer? answer, byte responseCode = 0)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (responseCode > 5 || (answer is not null && (responseCode != 0 || question.Class != 1
            || question.Type is not (1 or 28) || answer.Address.IsIPv4MappedToIPv6
            || answer.Address.GetAddressBytes().Length != (question.Type == 1 ? 4 : 16))))
        {
            throw new ArgumentException("Unsupported fixture DNS response.");
        }
        byte[] encoded = Question(question);
        byte[] address = answer?.Address.GetAddressBytes() ?? [];
        byte[] message = new byte[12 + encoded.Length + (answer is null ? 0 : 12 + address.Length)];
        Write16(message, 0, question.Id);
        Write16(message, 2, (ushort)(0x8180 | responseCode));
        Write16(message, 4, 1);
        Write16(message, 6, answer is null ? (ushort)0 : (ushort)1);
        encoded.CopyTo(message, 12);
        if (answer is not null)
        {
            int offset = 12 + encoded.Length;
            Write16(message, offset, 0xC00C);
            Write16(message, offset + 2, question.Type);
            Write16(message, offset + 4, question.Class);
            BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(offset + 6), Math.Min(answer.Ttl, 60));
            Write16(message, offset + 10, (ushort)address.Length);
            address.CopyTo(message, offset + 12);
        }
        return message;
    }

    public static byte[] MalformedResponse(ReadOnlySpan<byte> query)
    {
        byte[] response = new byte[12];
        if (query.Length >= 2)
        {
            query[..2].CopyTo(response);
        }
        Write16(response, 2, 0x8181); // FORMERR, no invented/partially parsed question.
        return response;
    }

    public static byte[] Frame(ReadOnlySpan<byte> message)
    {
        if (!Header(message))
        {
            throw new ArgumentException("DNS fixture message length is outside its bounds.", nameof(message));
        }
        byte[] framed = new byte[message.Length + 2];
        Write16(framed, 0, (ushort)message.Length);
        message.CopyTo(framed.AsSpan(2));
        return framed;
    }

    public static bool TryFrameLength(ReadOnlySpan<byte> prefix, out int length)
    {
        int candidate = prefix.Length == 2 ? Read16(prefix, 0) : 0;
        length = candidate is >= 12 and <= MaximumMessageBytes ? candidate : 0;
        return length != 0;
    }

    private static bool Header(ReadOnlySpan<byte> message) => message.Length is >= 12 and <= MaximumMessageBytes;

    private static bool ReadQuestion(ReadOnlySpan<byte> message, ref int offset, out DnsQuestion? question)
    {
        question = null;
        if (!ReadName(message, ref offset, out string? name) || offset + 4 > message.Length)
        {
            return false;
        }
        question = new DnsQuestion(Read16(message, 0), name!, Read16(message, offset), Read16(message, offset + 2));
        offset += 4;
        return true;
    }

    private static bool ReadName(ReadOnlySpan<byte> message, ref int offset, out string? name)
    {
        name = null;
        int start = offset;
        var text = new StringBuilder(64);
        while (offset < message.Length)
        {
            int length = message[offset++];
            if (length == 0)
            {
                if (text.Length == 0 || offset - start > 255)
                {
                    return false;
                }
                name = text.ToString();
                return true;
            }
            if (length > 63 || offset + length > message.Length || offset - start + length > 254
                || message[offset] == '-' || message[offset + length - 1] == '-')
            {
                return false;
            }
            if (text.Length != 0)
            {
                text.Append('.');
            }
            for (int index = 0; index < length; index++)
            {
                byte value = message[offset++];
                if (value is not (>= (byte)'a' and <= (byte)'z') and not (>= (byte)'A' and <= (byte)'Z')
                    and not (>= (byte)'0' and <= (byte)'9') and not (byte)'-')
                {
                    return false;
                }
                text.Append(value is >= (byte)'A' and <= (byte)'Z' ? (char)(value + 32) : (char)value);
            }
        }
        return false;
    }

    private static byte[] Question(DnsQuestion question)
    {
        string name = CanonicalName(question.Name);
        using var bytes = new MemoryStream(260);
        foreach (string label in name.Split('.'))
        {
            bytes.WriteByte((byte)label.Length);
            foreach (char value in label)
            {
                bytes.WriteByte((byte)char.ToLowerInvariant(value));
            }
        }
        bytes.WriteByte(0);
        Span<byte> fields = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(fields, question.Type);
        BinaryPrimitives.WriteUInt16BigEndian(fields[2..], question.Class);
        bytes.Write(fields);
        return bytes.ToArray();
    }

    internal static string CanonicalName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 254)
        {
            throw new ArgumentException("DNS fixture hostname exceeds its bounds.", nameof(value));
        }
        string name = value.EndsWith('.') ? value[..^1] : value;
        if (name.Length is 0 or > 253 || name.Split('.').Any(label => label.Length is 0 or > 63
            || label[0] == '-' || label[^1] == '-' || label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            throw new ArgumentException("DNS fixture requires bounded LDH hostname labels.", nameof(value));
        }
        // Labels were restricted to ASCII above; use exactly the wire reader's fold.
        return string.Create(name.Length, name, static (buffer, text) =>
        {
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                buffer[index] = character is >= 'A' and <= 'Z' ? (char)(character + 32) : character;
            }
        });
    }

    private static ushort Read16(ReadOnlySpan<byte> message, int offset) => BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
    private static void Write16(Span<byte> message, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(message[offset..], value);
}
