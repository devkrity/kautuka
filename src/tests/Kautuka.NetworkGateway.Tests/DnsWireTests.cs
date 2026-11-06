using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

using Kautuka.NetworkGateway.Dns;

namespace Kautuka.NetworkGateway.Tests;

public sealed class DnsWireTests
{
    private static readonly DnsQuestion question = new(0x1234, "allowed.core.ktng.test", 1, 1);

    [Fact]
    public void IndependentWireVectorsValidateQuestionAndCompressedAnswerLayout()
    {
        byte[] query = Convert.FromHexString("123401000001000000000000076578616d706c6503636f6d0000010001");
        byte[] response = Convert.FromHexString("123481800001000100000000076578616d706c6503636f6d0000010001c00c000100010000001e0004c0000207");
        var expected = new DnsQuestion(0x1234, "example.com", 1, 1);
        Assert.Equal(query, DnsWire.Query(expected));
        Assert.True(DnsWire.TryParseQuery(query, out DnsQuestion? parsed));
        Assert.Equal(expected, parsed);
        Assert.True(DnsWire.TryParseAnswer(response, expected, out DnsAnswer? answer));
        Assert.Equal(new DnsAnswer(IPAddress.Parse("192.0.2.7"), 30), answer);
        Assert.Equal(response, DnsWire.Response(expected, answer));
        byte[] uncompressed = Convert.FromHexString("123481800001000100000000076578616d706c6503636f6d0000010001076578616d706c6503636f6d00000100010000001e0004c0000207");
        Assert.True(DnsWire.TryParseAnswer(uncompressed, expected, out DnsAnswer? fullName));
        Assert.Equal(answer, fullName);
    }

    [Theory]
    [InlineData(1, "192.0.2.7")]
    [InlineData(28, "2001:db8::7")]
    public void SingleAddressAnswerMatchesOriginalQuestionAndCapsTtl(ushort type, string address)
    {
        DnsQuestion query = question with { Type = type };
        Assert.True(DnsWire.TryParseQuery(DnsWire.Query(query), out DnsQuestion? parsed));
        Assert.Equal(query, parsed);
        byte[] response = DnsWire.Response(query, new DnsAnswer(IPAddress.Parse(address), 600));
        Assert.True(DnsWire.TryParseAnswer(response, query, out DnsAnswer? answer));
        Assert.Equal(IPAddress.Parse(address), answer!.Address);
        Assert.Equal(60u, answer.Ttl);
    }

    [Fact]
    public void NamesNormalizeAsciiCaseAndTrailingRootForPolicyMatching()
    {
        byte[] query = DnsWire.Query(question with { Name = "ALLOWED.Core.Ktng.Test." });
        Assert.True(DnsWire.TryParseQuery(query, out DnsQuestion? parsed));
        Assert.Equal(question.Name, parsed!.Name);
        DnsQuestion caller = question with { Name = "ALLOWED.Core.Ktng.Test." };
        Assert.True(DnsWire.TryParseAnswer(DnsWire.Response(caller, new DnsAnswer(IPAddress.Parse("192.0.2.7"), 30)), caller, out _));
        // Also accept a client that sends upper-case wire labels directly.
        query[13] = (byte)'A';
        Assert.True(DnsWire.TryParseQuery(query, out parsed));
        Assert.Equal(question.Name, parsed!.Name);
    }

    [Fact]
    public void EveryTruncatedQueryAndAnswerIsRejectedWithoutThrowing()
    {
        byte[] query = DnsWire.Query(question);
        byte[] response = DnsWire.Response(question, new DnsAnswer(IPAddress.Parse("192.0.2.7"), 30));
        for (int length = 0; length < query.Length; length++)
        {
            Assert.False(DnsWire.TryParseQuery(query.AsSpan(0, length), out _));
        }
        for (int length = 0; length < response.Length; length++)
        {
            Assert.False(DnsWire.TryParseAnswer(response.AsSpan(0, length), question, out _));
        }
    }

    [Fact]
    public void ForgedIdQuestionTypeClassAndNameCannotMatchAResponse()
    {
        byte[] response = DnsWire.Response(question, new DnsAnswer(IPAddress.Parse("192.0.2.7"), 30));
        foreach (DnsQuestion mismatched in new[] {
            question with { Id = 0x1235 }, question with { Type = 28 },
            question with { Class = 3 }, question with { Name = "other.core.ktng.test" },
        })
        {
            Assert.False(DnsWire.TryParseAnswer(response, mismatched, out _));
        }
    }

    [Theory]
    [InlineData(2, 0x8380)] // Truncation is not accepted as a complete answer.
    [InlineData(6, 2)] // Multiple answers require later interoperability work.
    [InlineData(8, 1)]
    [InlineData(10, 1)]
    public void ExpandedOrTruncatedResponseFormsAreRejected(int offset, ushort value)
    {
        byte[] response = DnsWire.Response(question, new DnsAnswer(IPAddress.Parse("192.0.2.7"), 30));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset), value);
        Assert.False(DnsWire.TryParseAnswer(response, question, out _));
    }

    [Fact]
    public void CompressionCyclesAndUnmatchedResourceNamesAreNotFollowed()
    {
        byte[] query = DnsWire.Query(question);
        query[12] = 0xC0;
        query[13] = 12;
        Assert.False(DnsWire.TryParseQuery(query, out _));
        byte[] response = DnsWire.Response(question, new DnsAnswer(IPAddress.Parse("192.0.2.7"), 30));
        response[DnsWire.Query(question).Length + 1] = (byte)DnsWire.Query(question).Length;
        Assert.False(DnsWire.TryParseAnswer(response, question, out _));
    }

    [Fact]
    public void UnknownTypesRemainVisibleToPolicyRatherThanBecomingAddresses()
    {
        Assert.True(DnsWire.TryParseQuery(DnsWire.Query(question with { Type = 16 }), out DnsQuestion? parsed));
        Assert.Equal(question.Name, parsed!.Name);
        Assert.Equal((ushort)16, parsed.Type);
        byte[] refused = DnsWire.Response(parsed, null, responseCode: 5);
        Assert.Equal(5, refused[3] & 15);
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16BigEndian(refused));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(refused.AsSpan(6)));
    }

    [Fact]
    public void MessageTrailersAndMappedAddressesAreRejected()
    {
        byte[] query = DnsWire.Query(question);
        Assert.False(DnsWire.TryParseQuery([.. query, 0], out _));
        DnsQuestion ipv6 = question with { Type = 28 };
        byte[] response = DnsWire.Response(ipv6, new DnsAnswer(IPAddress.Parse("2001:db8::7"), 30));
        IPAddress.Parse("::ffff:192.0.2.7").GetAddressBytes().CopyTo(response, response.Length - 16);
        Assert.False(DnsWire.TryParseAnswer(response, ipv6, out _));
    }

    [Fact]
    public void HostnameLengthAndLabelSyntaxAreBounded()
    {
        string maximum = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        Assert.True(DnsWire.TryParseQuery(DnsWire.Query(question with { Name = maximum }), out _));
        foreach (string name in new[] { maximum + "d", new string('a', 64), new string('a', 255), "-bad.example", "bad-.example", "bad..example", "bäd.example" })
        {
            Assert.Throws<ArgumentException>(() => DnsWire.Query(question with { Name = name }));
        }
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 0x8100)]
    [InlineData(4, 2)]
    [InlineData(6, 1)]
    [InlineData(8, 1)]
    [InlineData(10, 1)]
    public void UnsupportedQueryHeadersDoNotBecomeQuestions(int offset, ushort value)
    {
        byte[] query = DnsWire.Query(question);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(offset), value);
        Assert.False(DnsWire.TryParseQuery(query, out _));
    }

    [Fact]
    public void HostileBoundedMessagesNeverEscapeParserBounds()
    {
        for (int index = 0; index < 512; index++)
        {
            byte[] message = new byte[RandomNumberGenerator.GetInt32(DnsWire.MaximumMessageBytes + 2)];
            RandomNumberGenerator.Fill(message);
            if (message.Length >= 12)
            {
                message.AsSpan(0, 12).Clear();
                BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 0x0100);
                BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 1);
            }
            byte[] mutated = DnsWire.Response(question, new DnsAnswer(IPAddress.Parse("192.0.2.7"), 30));
            mutated[RandomNumberGenerator.GetInt32(mutated.Length)] = (byte)RandomNumberGenerator.GetInt32(256);
            Exception? error = Record.Exception(() =>
            {
                _ = DnsWire.TryParseQuery(message, out _);
                _ = DnsWire.TryParseAnswer(mutated, question, out _);
            });
            if (error is not null)
            {
                Assert.Fail($"Parser escaped for query {Convert.ToHexString(message)} or reply {Convert.ToHexString(mutated)}: {error}");
            }
        }
    }

    [Fact]
    public void TcpPrefixCannotAllocateOutsideTheDeclaredMessageBound()
    {
        byte[] query = DnsWire.Query(question);
        byte[] frame = DnsWire.Frame(query);
        Assert.True(DnsWire.TryFrameLength(frame.AsSpan(0, 2), out int length));
        Assert.Equal(query.Length, length);
        Assert.Equal(query, frame[2..]);
        Assert.False(DnsWire.TryFrameLength([255, 255], out length));
        Assert.Equal(0, length);
        Assert.False(DnsWire.TryFrameLength([0, 11], out _));
        Assert.Throws<ArgumentException>(() => DnsWire.Frame(new byte[DnsWire.MaximumMessageBytes + 1]));
    }
}
