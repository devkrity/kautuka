using System.Globalization;
using System.Text.RegularExpressions;

using Kautuka.NetworkAccess.Protos.Envoy.Service.Auth.V3;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed partial class U3_ProtoCompatibilityTests
{
    [Fact]
    public void U3_VendoredExternalAuthProto_MatchesUpstreamFieldNumbers_WithNegativeControl()
    {
        string baseDir = AppContext.BaseDirectory;
        string vendoredPath = Path.Combine(baseDir, "Protos", "external_auth.proto");
        string upstreamPath = Path.Combine(baseDir, "Upstream", "external_auth.v1.40.2.proto");

        Assert.True(File.Exists(vendoredPath), $"Missing vendored proto at {vendoredPath}");
        Assert.True(File.Exists(upstreamPath), $"Missing upstream reference proto at {upstreamPath}");

        string vendoredText = File.ReadAllText(vendoredPath);
        string upstreamText = File.ReadAllText(upstreamPath);

        AssertProtoCompatibility(vendoredText, upstreamText);

        // Also verify compiled C# descriptors match critical wire field numbers
        Assert.Equal(1, CheckRequest.AttributesFieldNumber);
        Assert.Equal(1, AttributeContext.SourceFieldNumber);
        Assert.Equal(2, AttributeContext.DestinationFieldNumber);
        Assert.Equal(12, AttributeContext.TlsSessionFieldNumber);
        Assert.Equal(1, Peer.AddressFieldNumber);
        Assert.Equal(2, Peer.ServiceFieldNumber);
        Assert.Equal(1, Address.SocketAddressFieldNumber);
        Assert.Equal(1, SocketAddress.ProtocolFieldNumber);
        Assert.Equal(2, SocketAddress.AddressFieldNumber);
        Assert.Equal(3, SocketAddress.PortValueFieldNumber);
        Assert.Equal(1, TLSSession.SniFieldNumber);
        Assert.Equal(1, CheckResponse.StatusFieldNumber);
        Assert.Equal(4, CheckResponse.DynamicMetadataFieldNumber);
        Assert.Equal(1, Status.CodeFieldNumber);
        Assert.Equal(2, Status.MessageFieldNumber);

        // Negative control (.NC): mutate a field number in a copy of the vendored proto -> comparison MUST fail
        string mutatedVendored = vendoredText.Replace(
            "google.protobuf.Struct dynamic_metadata = 4;",
            "google.protobuf.Struct dynamic_metadata = 5;",
            StringComparison.Ordinal);
        Assert.NotEqual(vendoredText, mutatedVendored);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            AssertProtoCompatibility(mutatedVendored, upstreamText));
        Assert.Contains("dynamic_metadata", ex.Message, StringComparison.Ordinal);
    }

    private static void AssertProtoCompatibility(string vendoredProtoText, string upstreamProtoText)
    {
        Dictionary<string, Dictionary<string, int>> vendoredMessages = ParseProtoMessages(vendoredProtoText);
        Dictionary<string, Dictionary<string, int>> upstreamMessages = ParseProtoMessages(upstreamProtoText);

        Assert.NotEmpty(vendoredMessages);
        foreach ((string messageName, Dictionary<string, int> vendoredFields) in vendoredMessages)
        {
            if (!upstreamMessages.TryGetValue(messageName, out Dictionary<string, int>? upstreamFields))
            {
                throw new InvalidOperationException($"Message '{messageName}' in vendored proto does not exist in upstream proto.");
            }

            foreach ((string fieldName, int vendoredNumber) in vendoredFields)
            {
                if (!upstreamFields.TryGetValue(fieldName, out int upstreamNumber))
                {
                    throw new InvalidOperationException($"Field '{messageName}.{fieldName}' does not exist in upstream proto.");
                }

                if (vendoredNumber != upstreamNumber)
                {
                    throw new InvalidOperationException(
                        $"Field '{messageName}.{fieldName}' has number {vendoredNumber} in vendored proto but {upstreamNumber} in upstream proto.");
                }
            }
        }
    }

    private static Dictionary<string, Dictionary<string, int>> ParseProtoMessages(string protoText)
    {
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (Match messageMatch in MessageBlockRegex().Matches(protoText))
        {
            string messageName = messageMatch.Groups["name"].Value;
            string body = messageMatch.Groups["body"].Value;
            var fields = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match fieldMatch in FieldLineRegex().Matches(body))
            {
                string fieldName = fieldMatch.Groups["field"].Value;
                int number = int.Parse(fieldMatch.Groups["num"].Value, CultureInfo.InvariantCulture);
                fields[fieldName] = number;
            }

            result[messageName] = fields;
        }

        return result;
    }

    [GeneratedRegex(@"message\s+(?<name>[A-Za-z0-9_]+)\s*\{(?<body>[^\}]+)\}", RegexOptions.Multiline)]
    private static partial Regex MessageBlockRegex();

    [GeneratedRegex(@"(?:map<[^>]+>|[A-Za-z0-9_.]+)\s+(?<field>[A-Za-z0-9_]+)\s*=\s*(?<num>\d+)\s*;", RegexOptions.Multiline)]
    private static partial Regex FieldLineRegex();
}
