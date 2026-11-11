using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Kautuka.NetworkAccessController;

public sealed record NetworkAccessActor(string UserId, bool IsAdmin);

public sealed record NetworkAccessRule(
    string Id,
    string ScopeKind,
    string? UserId,
    string? AgentId,
    string? ConnectorId,
    string Destination,
    string DestKind,
    int Port,
    bool Allowed,
    bool AllowLan,
    string DecidedBy,
    DateTimeOffset DecidedAtUtc,
    string Source = "user")
{
    public static string ComputeId(
        string scopeKind,
        string? userId,
        string? agentId,
        string? connectorId,
        string destKind,
        string destination,
        int port)
        => NetworkAccessIdGenerator.ComputeBase32Id(
            $"{scopeKind}|{userId ?? string.Empty}|{agentId ?? string.Empty}|{connectorId ?? string.Empty}|{destKind}|{destination}|{port.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record NetworkAccessRuleView(
    NetworkAccessRule Rule,
    bool Paused,
    bool OpenInternetBadge);

public sealed record NetworkAccessRequest(
    string Id,
    string? UserId,
    string? AgentId,
    string? ConnectorId,
    bool Systemwide,
    string Attribution,
    string Destination,
    string DestKind,
    int Port,
    string ActorKind,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    long Attempts,
    string Status,
    string Origin,
    string? Purpose = null,
    string? CompatProfile = null)
{
    public static string ComputeId(
        string actorKind,
        string attribution,
        string? userId,
        string? agentId,
        string? connectorId,
        bool systemwide,
        string origin,
        string destKind,
        string destination,
        int port)
        => NetworkAccessIdGenerator.ComputeBase32Id(
            $"{actorKind}|{attribution}|{userId ?? string.Empty}|{agentId ?? string.Empty}|{connectorId ?? string.Empty}|{(systemwide ? "1" : "0")}|{origin}|{destKind}|{destination}|{port.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record NetworkAccessRequestView(
    NetworkAccessRequest Request,
    bool WaitingForAdmin,
    bool CompatibilityMode);

public sealed record UserPrivileges(
    string UserId,
    bool CanApproveOwn,
    bool CanAllowAllAgents,
    string SetBy,
    DateTimeOffset SetAtUtc);

public sealed record UserBrakeEntry(
    string UserId,
    string SetBy,
    DateTimeOffset SetAtUtc,
    bool LockedByAdmin = false);

public sealed record BrakeState(
    bool System,
    string? SystemSetBy,
    DateTimeOffset? SystemSetAtUtc,
    IReadOnlyList<UserBrakeEntry> Users)
{
    public static BrakeState Empty { get; } = new(false, null, null, []);

    public bool IsUserBraked(string? userId)
        => !string.IsNullOrWhiteSpace(userId)
           && Users.Any(u => string.Equals(u.UserId, userId, StringComparison.Ordinal));

    public UserBrakeEntry? GetUserBrake(string? userId)
        => string.IsNullOrWhiteSpace(userId)
            ? null
            : Users.FirstOrDefault(u => string.Equals(u.UserId, userId, StringComparison.Ordinal));
}

public sealed record NetworkAccessPolicyState(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<NetworkAccessRule> Rules,
    IReadOnlyList<NetworkAccessRequest> Requests,
    IReadOnlyList<UserPrivileges> UserPrivileges,
    BrakeState Brake);

public sealed record RuleWriteSpec(
    string ScopeKind,
    string? UserId,
    string? AgentId,
    string? ConnectorId,
    string Destination,
    string DestKind,
    int Port,
    bool Allowed,
    bool AllowLan = false,
    string? ExecContext = null,
    string? Attribution = null);

public sealed record DecideRequestResult(
    long Revision,
    NetworkAccessRule Rule,
    NetworkAccessRequest Request,
    bool Paused,
    bool OpenInternetBadge);

public sealed record NetworkAccessDecisionResult(
    bool Allowed,
    string Reason,
    string? Cluster = null,
    string? RequestId = null,
    bool OpenInternetBadge = false);

public sealed record ContainerConnectionInput(
    string? SourceIp,
    string? DestinationIp,
    int DestinationPort,
    string? Sni,
    bool? IsTls = null);

public sealed record OutboundCheckInput(
    string UserId,
    string AgentId,
    string? ConnectorId,
    bool Systemwide,
    string Host,
    int Port,
    string? Purpose,
    string? CorrelationId,
    string Origin = "gateway");

public sealed record NetworkAccessActivityEntry(
    DateTimeOffset TimestampUtc,
    string? SourceIp,
    string? UserId,
    string? AgentId,
    string? ConnectorId,
    string Destination,
    int Port,
    string Decision,
    string Reason,
    string? Cluster,
    bool OpenInternetBadge,
    bool CompatibilityMode);

public sealed record SeedRuleConfigEntry(
    string ScopeKind,
    string UserId,
    string AgentId,
    string Destination,
    string DestKind,
    int Port,
    bool Allowed,
    bool AllowLan);

public sealed record SeedRulesFile(
    int SchemaVersion,
    IReadOnlyList<SeedRuleConfigEntry> Seeds);

public sealed record ImageCompatProfile(
    string ImageDigest,
    string? ConnectorId,
    string? ConnectorDisplayName,
    IReadOnlyList<string> Relaxations,
    string GrantedBy,
    DateTimeOffset GrantedAtUtc,
    string? Reason);

public sealed record ImageCompatRequest(
    string ImageDigest,
    string ConnectorId,
    string? ConnectorDisplayName,
    string FailureReason,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

public sealed record ImageCompatState(
    int SchemaVersion,
    IReadOnlyList<ImageCompatProfile> Profiles,
    IReadOnlyList<ImageCompatRequest> PendingRequests);

public sealed record NetworkAccessStatusSnapshot(
    long Revision,
    int RegistrySize,
    long Checks,
    long Allowed,
    long Denied,
    long Errors,
    long RequestOverflow,
    long RegistryConflicts,
    bool EnvoyListenerHealthy);

public static class NetworkAccessIdGenerator
{
    private const string base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>
    /// Computes the first 26 characters of lowercase RFC 4648 base32(sha256(input)).
    /// </summary>
    public static string ComputeBase32Id(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);

        Span<char> chars = stackalloc char[26];
        int buffer = hash[0];
        int next = 1;
        int bitsLeft = 8;
        for (int i = 0; i < 26; i++)
        {
            if (bitsLeft < 5)
            {
                if (next < hash.Length)
                {
                    buffer <<= 8;
                    buffer |= hash[next++] & 0xff;
                    bitsLeft += 8;
                }
                else
                {
                    int pad = 5 - bitsLeft;
                    buffer <<= pad;
                    bitsLeft += pad;
                }
            }

            int index = 0x1f & (buffer >> (bitsLeft - 5));
            bitsLeft -= 5;
            chars[i] = base32Alphabet[index];
        }

        return new string(chars);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NetworkAccessPolicyState))]
[JsonSerializable(typeof(SeedRulesFile))]
[JsonSerializable(typeof(ImageCompatState))]
[JsonSerializable(typeof(NetworkAccessActivityEntry))]
internal sealed partial class NetworkAccessJsonContext : JsonSerializerContext
{
}
