using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.NetworkAccessController;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

internal sealed record WorkloadAddressAllocatorCursorState(
    [property: JsonPropertyName("cursorIndex")] int CursorIndex,
    [property: JsonPropertyName("lastAllocatedIp")] string? LastAllocatedIp,
    [property: JsonPropertyName("updatedUtc")] DateTimeOffset UpdatedUtc);

[JsonSerializable(typeof(WorkloadAddressAllocatorCursorState))]
internal sealed partial class WorkloadAddressAllocatorJsonContext : JsonSerializerContext;

/// <summary>
/// Allocates IPv4 addresses for workload containers from the range 10.247.1.1 to 10.247.255.254
/// (never 10.247.0.0/24, never .0 host, never .255 broadcast).
/// Maintains a persistent monotonic cursor in &lt;manager-state&gt;/network-access/ip-cursor.json,
/// skips IPs currently registered in IWorkloadRegistry or live on the network, and wraps cleanly
/// after 10.247.255.254 (total 64,770 addresses).
/// </summary>
public sealed class WorkloadAddressAllocator
{
    public const string DefaultStateFileName = "ip-cursor.json";
    public const int UsableHostsPerSubnet = 254; // .1 to .254
    public const int UsableSubnets = 255;        // .1 to .255 (skipping 10.247.0.0/24)
    public const int TotalAddressPoolSize = UsableSubnets * UsableHostsPerSubnet; // 64,770

    private readonly object gate = new();
    private readonly string stateFilePath;
    private int cursorIndex = -1;
    private string? lastAllocatedIp;

    public WorkloadAddressAllocator(string stateFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        this.stateFilePath = Path.GetFullPath(stateFilePath);
        LoadCursor();
    }

    public int CurrentCursorIndex
    {
        get
        {
            lock (gate)
            {
                return cursorIndex;
            }
        }
    }

    public string? LastAllocatedIp
    {
        get
        {
            lock (gate)
            {
                return lastAllocatedIp;
            }
        }
    }

    /// <summary>
    /// Allocates the next monotonic IPv4 address, advancing the cursor, skipping any IP currently
    /// in use, and wrapping cleanly from 10.247.255.254 back to 10.247.1.1.
    /// </summary>
    public string AllocateNextIp(
        IWorkloadRegistry? registry = null,
        Func<string, bool>? isLiveOnNetwork = null)
    {
        lock (gate)
        {
            for (int attempt = 0; attempt < TotalAddressPoolSize; attempt++)
            {
                cursorIndex = (cursorIndex + 1) % TotalAddressPoolSize;
                string candidateIp = ComputeIpFromIndex(cursorIndex);

                // Skip if currently in workload registry:
                if (registry is not null && registry.TryGetByIp(candidateIp, out _))
                {
                    continue;
                }

                // Skip if live on network:
                if (isLiveOnNetwork is not null && isLiveOnNetwork(candidateIp))
                {
                    continue;
                }

                lastAllocatedIp = candidateIp;
                SaveCursor(cursorIndex, lastAllocatedIp);
                return candidateIp;
            }

            throw new InvalidOperationException("Workload address space (10.247.1.1 - 10.247.255.254) is exhausted; all 64,770 addresses are in use.");
        }
    }

    /// <summary>
    /// Computes the IPv4 string for a 0-based index in [0, 64769].
    /// Index 0 -> 10.247.1.1, Index 253 -> 10.247.1.254, Index 254 -> 10.247.2.1, Index 64769 -> 10.247.255.254.
    /// </summary>
    public static string ComputeIpFromIndex(int index)
    {
        if (index < 0 || index >= TotalAddressPoolSize)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be between 0 and {TotalAddressPoolSize - 1}.");
        }

        int subnetOctet = 1 + (index / UsableHostsPerSubnet);
        int hostOctet = 1 + (index % UsableHostsPerSubnet);
        return string.Create(CultureInfo.InvariantCulture, $"10.247.{subnetOctet}.{hostOctet}");
    }

    /// <summary>
    /// Computes the 0-based index for a valid workload IP (10.247.1.1 to 10.247.255.254).
    /// </summary>
    public static bool TryGetIndexFromIp(string? ip, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out IPAddress? parsed))
        {
            return false;
        }

        byte[] bytes = parsed.GetAddressBytes();
        if (bytes.Length != 4 || bytes[0] != 10 || bytes[1] != 247)
        {
            return false;
        }

        int subnetOctet = bytes[2];
        int hostOctet = bytes[3];

        if (subnetOctet < 1 || subnetOctet > 255)
        {
            return false; // 10.247.0.0/24 is reserved for egress pod and package proxy
        }

        if (hostOctet < 1 || hostOctet > 254)
        {
            return false; // .0 and .255 are network and broadcast addresses
        }

        index = ((subnetOctet - 1) * UsableHostsPerSubnet) + (hostOctet - 1);
        return true;
    }

    private void LoadCursor()
    {
        lock (gate)
        {
            if (!File.Exists(stateFilePath))
            {
                cursorIndex = -1;
                lastAllocatedIp = null;
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(stateFilePath);
                var loaded = JsonSerializer.Deserialize(bytes, WorkloadAddressAllocatorJsonContext.Default.WorkloadAddressAllocatorCursorState);
                if (loaded is not null && loaded.CursorIndex >= 0 && loaded.CursorIndex < TotalAddressPoolSize)
                {
                    cursorIndex = loaded.CursorIndex;
                    lastAllocatedIp = loaded.LastAllocatedIp;
                }
            }
            catch
            {
                cursorIndex = -1;
                lastAllocatedIp = null;
            }
        }
    }

    private void SaveCursor(int index, string? ip)
    {
        string directory = Path.GetDirectoryName(stateFilePath)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string tempPath = Path.Combine(directory, ".ip-cursor-" + Guid.NewGuid().ToString("N") + ".tmp");
        var state = new WorkloadAddressAllocatorCursorState(index, ip, DateTimeOffset.UtcNow);

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(stream, state, WorkloadAddressAllocatorJsonContext.Default.WorkloadAddressAllocatorCursorState);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, stateFilePath, overwrite: true);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(stateFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
