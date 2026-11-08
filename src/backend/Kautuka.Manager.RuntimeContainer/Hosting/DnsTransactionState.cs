using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed record DnsRevision(string Corefile, string CanaryName, string CanaryAddress, IReadOnlyList<string> Upstreams);
internal sealed record DnsTransactionState(DnsRevision? LastGood, DnsRevision? Pending)
{
    internal string Serialize()
    {
        var root = new JsonObject { ["version"] = 1, ["lastGood"] = Node(LastGood), ["pending"] = Node(Pending) };
        return root.ToJsonString();
    }

    internal static DnsTransactionState Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported DNS transaction state version.");
        var state = new DnsTransactionState(Read(root.GetProperty("lastGood")), Read(root.GetProperty("pending")));
        if (state.LastGood is null && state.Pending is null) throw new InvalidDataException("DNS transaction state has no revision.");
        return state;
    }

    private static JsonObject? Node(DnsRevision? revision)
    {
        if (revision is null) return null;
        var upstreams = new JsonArray();
        foreach (string upstream in revision.Upstreams) upstreams.Add((JsonNode?)JsonValue.Create(upstream));
        return new JsonObject
        {
            ["corefile"] = revision.Corefile,
            ["canaryName"] = revision.CanaryName,
            ["canaryAddress"] = revision.CanaryAddress,
            ["upstreams"] = upstreams
        };
    }

    private static DnsRevision? Read(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        string corefile = element.GetProperty("corefile").GetString() ?? throw new InvalidDataException("DNS transaction has no Corefile.");
        string name = element.GetProperty("canaryName").GetString() ?? throw new InvalidDataException("DNS transaction has no canary.");
        string address = element.GetProperty("canaryAddress").GetString() ?? throw new InvalidDataException("DNS transaction has no canary address.");
        string[] upstreams = [.. element.GetProperty("upstreams").EnumerateArray().Select(static value => value.GetString() ?? string.Empty)];
        NetworkDestinationPolicy.ValidateDnsUpstreams(upstreams);
        const string suffix = ".kautuka-dns-readiness.invalid";
        string label = name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : string.Empty;
        if (corefile.Length is 0 or > 65536 || label.Length != 33 || label[0] != 'n' || !label[1..].All(char.IsAsciiHexDigit)
            || !IPAddress.TryParse(address, out IPAddress? parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || parsed.GetAddressBytes()[0] != 198 || parsed.GetAddressBytes()[1] != 18
            || address != parsed.ToString() || corefile != CoreDnsConfiguration.RenderVersion1(name, address, upstreams))
            throw new InvalidDataException("DNS transaction contains invalid canary metadata.");
        return new DnsRevision(corefile, name, parsed.ToString(), upstreams);
    }
}
