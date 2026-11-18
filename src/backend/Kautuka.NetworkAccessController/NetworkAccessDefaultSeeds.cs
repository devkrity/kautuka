using System.Reflection;
using System.Text.Json;

namespace Kautuka.NetworkAccessController;

public static class NetworkAccessDefaultSeeds
{
    public const string FileName = "network-access-defaults.json";
    private const string embeddedResourceName = "Kautuka.NetworkAccessController.network-access-defaults.json";
    private static readonly DateTimeOffset seedEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<NetworkAccessRule> DefaultRules { get; } = LoadDefaultSeeds();

    public static IReadOnlyList<NetworkAccessRule> LoadFromJson(ReadOnlySpan<byte> utf8Json)
    {
        SeedRulesFile file = JsonSerializer.Deserialize(utf8Json, NetworkAccessJsonContext.Default.SeedRulesFile)
            ?? throw new InvalidDataException("network-access-defaults.json is empty.");
        if (file.SchemaVersion != 1 || file.Seeds is null)
        {
            throw new InvalidDataException("Unsupported network-access-defaults.json schema.");
        }

        var rules = new List<NetworkAccessRule>(file.Seeds.Count);
        foreach (SeedRuleConfigEntry entry in file.Seeds)
        {
            (string destKind, string normalizedDest, int port) = NetworkDestinationNormalizer.ValidateAndNormalizeRuleDestination(
                entry.DestKind,
                entry.Destination,
                entry.Port,
                entry.Allowed,
                entry.AllowLan,
                isAdmin: true);
            string id = NetworkAccessRule.ComputeId(
                entry.ScopeKind,
                entry.UserId,
                entry.AgentId,
                connectorId: null,
                destKind,
                normalizedDest,
                port);
            rules.Add(new NetworkAccessRule(
                id,
                entry.ScopeKind,
                entry.UserId,
                entry.AgentId,
                ConnectorId: null,
                normalizedDest,
                destKind,
                port,
                entry.Allowed,
                entry.AllowLan,
                DecidedBy: "system",
                DecidedAtUtc: seedEpoch,
                Source: "seed"));
        }

        return rules;
    }

    private static IReadOnlyList<NetworkAccessRule> LoadDefaultSeeds()
    {
        string localFile = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(localFile))
        {
            return LoadFromJson(File.ReadAllBytes(localFile));
        }

        Assembly assembly = typeof(NetworkAccessDefaultSeeds).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(embeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{embeddedResourceName}' is missing.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return LoadFromJson(ms.ToArray());
    }
}
