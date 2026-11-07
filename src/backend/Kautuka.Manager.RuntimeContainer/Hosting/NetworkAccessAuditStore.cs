using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed record NetworkAccessAllocation(string Address, string ContainerId, NetworkAccessIdentity Identity);
public sealed record NetworkAuditEvent(long Sequence, string Id, DateTimeOffset StartedAt, DateTimeOffset ObservedAt,
    NetworkAccessIdentity? Identity, string? ContainerId, string SourceAddress, string Destination, int Port,
    string Outcome, bool Connected, bool Closed, long BytesSent, long BytesReceived, long DurationMilliseconds, string Publication);
public sealed record NetworkAuditPage(IReadOnlyList<NetworkAuditEvent> Events, long NextBefore, long CoverageGaps,
    long UnattributedConnections, string? LastError);
internal sealed record NetworkAuditState(int SchemaVersion, long Sequence, long CoverageGaps, long UnattributedConnections,
    string? EnvoyContainerId, DateTimeOffset? Cursor, IReadOnlyList<NetworkAuditEvent> Events, IReadOnlyList<string> ObservedLines)
{
    public string? LastLogLineId { get; init; }
}
internal sealed record NetworkAuditBatch(IReadOnlyList<NetworkDeniedObservation> Denials, int ParsedLines, bool Changed, string? LastLineId);

/// <summary>Bounded observations of stock proxy logs. It is independent of admission and never resumes a tool.</summary>
internal sealed class NetworkAccessAuditStore(string root)
{
    private const int eventLimit = 2048;
    private readonly object gate = new();
    private readonly string path = Path.Combine(root, "state.json");
    private readonly string generations = Path.Combine(root, "allocations");
    private readonly Dictionary<string, NetworkAccessAllocation[]> allocationCache = new(StringComparer.Ordinal);
    private NetworkAuditState? state;
    private IOException? storageFailure;

    internal string RecordAllocations(IEnumerable<NetworkAccessAllocation> allocations)
    {
        NetworkAccessAllocation[] ordered = [.. allocations.OrderBy(item => item.Address, StringComparer.Ordinal).ThenBy(item => item.ContainerId, StringComparer.Ordinal)];
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ordered, NetworkAccessAuditJsonContext.Default.NetworkAccessAllocationArray);
        if (bytes.Length > 8 * 1024 * 1024) throw new IOException("The audit allocation map exceeds its supported size.");
        string version = Hash(bytes);
        lock (gate)
        {
            CheckStorage();
            if (allocationCache.ContainsKey(version)) return version;
            string destination = Path.Combine(generations, version + ".json");
            try
            {
                byte[] existing = File.ReadAllBytes(destination);
                if (!existing.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("An immutable audit allocation map is corrupt.");
            }
            catch (FileNotFoundException) { Write(destination, bytes); }
            catch (DirectoryNotFoundException) { Write(destination, bytes); }
            if (allocationCache.Count >= 128) allocationCache.Clear();
            allocationCache[version] = ordered;
            // Events already contain their immutable identities. Older maps are
            // retained for delayed connection ends, within an explicit bound.
            foreach (FileInfo stale in new DirectoryInfo(generations).EnumerateFiles("*.json")
                .Where(file => file.Name.Length == 69 && IsHash(file.Name[..64]) && file.Name != version + ".json")
                .OrderByDescending(file => file.LastWriteTimeUtc).Skip(127))
            {
                File.Delete(stale.FullName);
                allocationCache.Remove(stale.Name[..64]);
            }
            return version;
        }
    }

    internal (string? ContainerId, DateTimeOffset? Cursor, string? LastLineId) Checkpoint
    {
        get { lock (gate) { NetworkAuditState current = Load(); return (current.EnvoyContainerId, current.Cursor, current.LastLogLineId ?? (current.ObservedLines.Count == 0 ? null : current.ObservedLines[^1])); } }
    }

    internal NetworkAuditPage Read(NetworkAccessActor actor, long before, int limit, string? error)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.UserId);
        ArgumentOutOfRangeException.ThrowIfNegative(before);
        limit = Math.Clamp(limit, 1, 100);
        lock (gate)
        {
            NetworkAuditState current = Load();
            NetworkAuditEvent[] rows = [.. current.Events.Where(item => (before == 0 || item.Sequence < before)
                && (actor.IsAdmin || item.Identity is not null && NetworkAccessPolicyStore.CanAccess(actor, item.Identity)))
                .OrderByDescending(item => item.Sequence).Take(limit + 1)];
            return new(rows.Take(limit).ToArray(), rows.Length > limit ? rows[limit - 1].Sequence : 0,
                current.CoverageGaps, actor.IsAdmin ? current.UnattributedConnections : 0, error);
        }
    }

    internal NetworkAuditBatch Observe(string envoyContainerId, IEnumerable<string> lines, bool coverageGap)
    {
        lock (gate)
        {
            NetworkAuditState current = Load();
            var events = current.Events.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var seen = current.ObservedLines.ToHashSet(StringComparer.Ordinal);
            var orderedSeen = current.ObservedLines.ToList();
            var denials = new List<NetworkDeniedObservation>();
            long sequence = current.Sequence;
            long unattributed = current.UnattributedConnections;
            int parsed = 0;
            string? lastLineId = null;
            foreach (string line in lines)
            {
                int separator = line.IndexOf(' ', StringComparison.Ordinal);
                if (separator <= 0 || !DateTimeOffset.TryParse(line.AsSpan(0, separator), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out DateTimeOffset observedAt)) continue;
                lastLineId = LogLineId(envoyContainerId, line);
                string json = line[(separator + 1)..];
                if (!json.StartsWith('{')) continue; // Stock Envoy diagnostics are not connection records.
                if (json.Length > 16384) { coverageGap = true; continue; }
                JsonDocument document;
                try { document = JsonDocument.Parse(json); }
                catch (JsonException) { coverageGap = true; continue; }
                using (document)
                {
                    JsonElement value = document.RootElement;
                    string phase = Text(value, "phase");
                    if (phase is not ("listener" or "proxy" or "host-artifact")) continue;
                    // Local ingestion shares the host socket but is not external artifact access.
                    string authorityValue = Text(value, "authority");
                    if (phase == "host-artifact" && Text(value, "request_method") == "POST"
                        && (authorityValue.Equals("kautuka-telemetry.internal", StringComparison.OrdinalIgnoreCase)
                            || authorityValue.Equals("kautuka-telemetry.internal:80", StringComparison.OrdinalIgnoreCase))) continue;
                    parsed++;
                    string rawId = Hash(Encoding.UTF8.GetBytes(envoyContainerId + "\0" + line));
                    string source = Text(value, "source");
                    string sourceAddress = Text(value, "source_address");
                    string connection = Text(value, "connection_id");
                    string started = Text(value, "connection_started");
                    bool correlated = connection.Length is > 0 and <= 32 && connection.All(char.IsAsciiDigit)
                        && ValidStart(started)
                        && IPEndPoint.TryParse(sourceAddress, out IPEndPoint? remote) && remote.Port > 0
                        && IPAddress.TryParse(source, out IPAddress? sourceIp) && remote.Address.Equals(sourceIp);
                    if (!correlated && phase != "host-artifact" && !seen.Contains(rawId)) coverageGap = true;
                    string id = correlated ? Hash(Encoding.UTF8.GetBytes(string.Join('\0', envoyContainerId, connection, started, sourceAddress))) : rawId;
                    NetworkAccessAllocation? allocation = Allocation(Text(value, "allocation"), source);
                    string destinationAddress = Text(value, "destination");
                    bool hasEndpoint = IPEndPoint.TryParse(destinationAddress, out IPEndPoint? endpoint);
                    string destination = Text(value, "sni");
                    if (string.IsNullOrEmpty(destination)) destination = hasEndpoint ? endpoint!.Address.ToString() : "unknown";
                    int port = hasEndpoint ? endpoint!.Port : 0;
                    if (phase == "host-artifact" && Uri.TryCreate("https://" + Text(value, "authority"), UriKind.Absolute, out Uri? authority))
                    {
                        destination = authority.IdnHost;
                        port = authority.Port;
                    }
                    bool denied = Text(value, "filter_chain") == "deny" && phase == "listener"
                        || phase == "host-artifact" && Number(value, "status") == 403;
                    bool connected = phase == "proxy" && Text(value, "event_type") == "TcpUpstreamConnected";
                    bool closed = phase == "listener" || Text(value, "event_type") is "TcpConnectionEnd" or "DownstreamEnd";
                    string outcome = denied ? "denied" : connected ? "connected" : closed ? "closed" : "diagnostic";
                    DateTimeOffset startedAt = DateTimeOffset.TryParse(Text(value, "timestamp"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out DateTimeOffset time) ? time : observedAt;
                    bool validDestination = false;
                    try
                    {
                        destination = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", destination, port)).Destination;
                        validDestination = true;
                    }
                    catch (ArgumentException) { destination = destination.Length > 253 ? destination[..253] : destination; }
                    if (denied && phase == "listener" && correlated && validDestination && allocation is not null)
                        denials.Add(new(allocation.Identity, destination, port, observedAt, id));
                    // Replay still supplies the denial to the idempotent policy
                    // writer if audit persistence succeeded before a crash.
                    if (!seen.Add(rawId)) continue;
                    orderedSeen.Add(rawId);
                    events.TryGetValue(id, out NetworkAuditEvent? prior);
                    if (prior is null && allocation is null) unattributed = checked(unattributed + 1);
                    if (prior is not null && observedAt < prior.ObservedAt) continue;
                    events[id] = new(checked(++sequence), id, prior?.StartedAt ?? startedAt, observedAt,
                        prior?.Identity ?? allocation?.Identity, prior?.ContainerId ?? allocation?.ContainerId,
                        sourceAddress.Length <= 128 ? sourceAddress : sourceAddress[..128], destination, port,
                        denied || prior?.Outcome == "denied" ? "denied" : closed || prior?.Closed == true ? "closed" : outcome,
                        connected || prior?.Connected == true, closed || prior?.Closed == true,
                        Math.Max(prior?.BytesSent ?? 0, Number(value, "bytes_sent")), Math.Max(prior?.BytesReceived ?? 0, Number(value, "bytes_received")),
                        Math.Max(prior?.DurationMilliseconds ?? 0, Number(value, "duration_ms")), Text(value, "publication"));
                }
            }
            bool changed = sequence != current.Sequence || coverageGap;
            if (changed)
                Save(current with
                {
                    Sequence = sequence,
                    Events = events.Values.OrderByDescending(item => item.Sequence).Take(eventLimit).ToArray(),
                    ObservedLines = orderedSeen.TakeLast(4096).ToArray(),
                    UnattributedConnections = unattributed,
                    CoverageGaps = checked(current.CoverageGaps + (coverageGap ? 1 : 0))
                });
            return new(denials, parsed, changed, lastLineId);
        }
    }

    internal void CommitCheckpoint(string envoyContainerId, DateTimeOffset until, string? lastLineId = null)
    {
        lock (gate) Save(Load() with { EnvoyContainerId = envoyContainerId, Cursor = until, LastLogLineId = lastLineId ?? Load().LastLogLineId });
    }

    private NetworkAccessAllocation? Allocation(string version, string address)
    {
        if (!IsHash(version)) return null;
        if (!allocationCache.TryGetValue(version, out NetworkAccessAllocation[]? allocations))
        {
            byte[] bytes;
            try
            {
                string file = Path.Combine(generations, version + ".json");
                if (new FileInfo(file).Length > 8 * 1024 * 1024) return null;
                bytes = File.ReadAllBytes(file);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if (Hash(bytes) != version) return null;
            allocations = JsonSerializer.Deserialize(bytes, NetworkAccessAuditJsonContext.Default.NetworkAccessAllocationArray)
                ?? throw new InvalidDataException("An audit allocation map is empty.");
            if (allocationCache.Count >= 128) allocationCache.Clear();
            allocationCache[version] = allocations;
        }
        return allocations.SingleOrDefault(item => item.Address == address);
    }

    private NetworkAuditState Load()
    {
        CheckStorage();
        if (state is not null) return state;
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("The audit store exceeds its supported size.");
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException) { return state = new(1, 0, 0, 0, null, null, [], []); }
        catch (DirectoryNotFoundException) { return state = new(1, 0, 0, 0, null, null, [], []); }
        NetworkAuditState loaded = JsonSerializer.Deserialize(bytes, NetworkAccessAuditJsonContext.Default.NetworkAuditState)
            ?? throw new InvalidDataException("The audit store is empty.");
        if (loaded.SchemaVersion != 1 || loaded.Sequence < 0 || loaded.CoverageGaps < 0 || loaded.UnattributedConnections < 0
            || loaded.Events is null || loaded.Events.Count > eventLimit || loaded.ObservedLines is null || loaded.ObservedLines.Count > 4096
            || loaded.Events.Any(item => item is null || !IsHash(item.Id) || item.Sequence <= 0 || item.Sequence > loaded.Sequence)
            || loaded.Events.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != loaded.Events.Count)
            throw new InvalidDataException("The audit store is invalid.");
        return state = loaded;
    }

    private void Save(NetworkAuditState next)
    {
        Write(path, JsonSerializer.SerializeToUtf8Bytes(next, NetworkAccessAuditJsonContext.Default.NetworkAuditState));
        state = next;
    }

    private void Write(string destination, byte[] bytes)
    {
        CheckStorage();
        string directory = Path.GetDirectoryName(destination)!;
        var missing = new List<string>();
        for (string? parent = directory; parent is not null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent)) missing.Add(parent);
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = Path.Combine(directory, ".audit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            NetworkAccessPolicyStore.FlushDirectory(directory);
            foreach (string created in missing)
            {
                string? parent = Path.GetDirectoryName(created);
                if (parent is not null) NetworkAccessPolicyStore.FlushDirectory(parent);
            }
        }
        catch (Exception ex) { storageFailure = new IOException("Audit persistence requires recovery.", ex); throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void CheckStorage() { if (storageFailure is not null) throw storageFailure; }
    private static bool IsHash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    internal static string LogLineId(string containerId, string line) => Hash(Encoding.UTF8.GetBytes(containerId + "\0" + line));
    private static string Text(JsonElement value, string name) => !value.TryGetProperty(name, out JsonElement item) ? string.Empty
        : item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ValueKind == JsonValueKind.Number ? item.GetRawText() : string.Empty;
    private static long Number(JsonElement value, string name) => long.TryParse(Text(value, name), NumberStyles.None, CultureInfo.InvariantCulture, out long result) ? result : 0;
    private static bool ValidStart(string value)
    {
        int point = value.IndexOf('.', StringComparison.Ordinal);
        return point > 0 && value.Length - point == 10 && value.AsSpan(point + 1).IndexOfAnyExceptInRange('0', '9') < 0
            && long.TryParse(value.AsSpan(0, point), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NetworkAuditState))]
[JsonSerializable(typeof(NetworkAccessAllocation[]))]
internal sealed partial class NetworkAccessAuditJsonContext : JsonSerializerContext;
