using System.Diagnostics;
using System.Text.Json;

using Kautuka.NetworkAccessController;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

public sealed record WorkloadVerificationResult(
    string ContainerId,
    string AllocatedIp,
    bool Passed,
    long ExposureWindowMs,
    string? FailureReason = null);

/// <summary>
/// Verifies live workload container compliance immediately after start (MF6, Section 4.3.4 item 4).
/// Inspects security attributes via Podman inspect and verifies the in-netns nftables ruleset
/// via nsenter and canonical hash comparison.
/// Immediately kills and removes any container failing verification.
/// </summary>
public sealed class WorkloadPostStartVerifier
{
    private readonly PodmanCommandRunner podmanRunner;

    public WorkloadPostStartVerifier(PodmanCommandRunner podmanRunner)
    {
        ArgumentNullException.ThrowIfNull(podmanRunner);
        this.podmanRunner = podmanRunner;
    }

    /// <summary>
    /// Executes the full post-start verification check. If any check fails, the container is killed
    /// and force-removed immediately before throwing an <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<WorkloadVerificationResult> VerifyWorkloadAsync(
        string containerId,
        string allocatedIp,
        ImageCompatProfile? compatProfile = null,
        IWorkloadRegistry? registry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(allocatedIp);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // 1. Inspect container
            PodmanCommandResult inspectResult = await podmanRunner
                .RunAsync(["inspect", containerId], 15, cancellationToken)
                .ConfigureAwait(false);

            if (inspectResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"podman inspect failed for '{containerId}' (rc {inspectResult.ExitCode}): {inspectResult.StdErr}");
            }

            using JsonDocument doc = JsonDocument.Parse(inspectResult.StdOut);
            JsonElement container = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement[0]
                : doc.RootElement;

            JsonElement hostConfig = container.GetProperty("HostConfig");
            JsonElement config = container.GetProperty("Config");
            JsonElement state = container.GetProperty("State");

            int pid = state.TryGetProperty("Pid", out JsonElement pidElem) ? pidElem.GetInt32() : 0;

            // 1a. Security options & privileges
            bool privileged = hostConfig.TryGetProperty("Privileged", out JsonElement privElem) && privElem.GetBoolean();
            if (privileged)
            {
                throw new InvalidOperationException("Privilege check failed: container is running with Privileged=true.");
            }

            // 1b. Capabilities
            bool allowFileCaps = compatProfile?.Relaxations.Contains(ImageCompatibilityStore.FileOwnershipCaps) == true;
            if (hostConfig.TryGetProperty("CapAdd", out JsonElement capAddElem) && capAddElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement capElem in capAddElem.EnumerateArray())
                {
                    string? cap = capElem.GetString();
                    if (string.IsNullOrWhiteSpace(cap))
                    {
                        continue;
                    }

                    if (!allowFileCaps || !WorkloadContainerSpec.AllowedFileOwnershipCaps.Contains(cap))
                    {
                        throw new InvalidOperationException($"Capability check failed: prohibited CapAdd '{cap}' found on live container.");
                    }
                }
            }

            bool capDropAll = false;
            if (hostConfig.TryGetProperty("CapDrop", out JsonElement capDropElem) && capDropElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement capElem in capDropElem.EnumerateArray())
                {
                    if (string.Equals(capElem.GetString(), "ALL", StringComparison.OrdinalIgnoreCase))
                    {
                        capDropAll = true;
                        break;
                    }
                }
            }

            if (!capDropAll)
            {
                throw new InvalidOperationException("Capability check failed: CapDrop does not include 'ALL'.");
            }

            // 1c. Readonly rootfs
            bool allowWritableRootfs = compatProfile?.Relaxations.Contains(ImageCompatibilityStore.WritableRootfs) == true;
            bool readonlyRootfs = hostConfig.TryGetProperty("ReadonlyRootfs", out JsonElement roElem) && roElem.GetBoolean();
            if (!allowWritableRootfs && !readonlyRootfs)
            {
                throw new InvalidOperationException("Filesystem check failed: ReadonlyRootfs is false without compatibility profile.");
            }

            // 1d. User
            bool allowRootUser = compatProfile?.Relaxations.Contains(ImageCompatibilityStore.RootUser) == true;
            string user = config.TryGetProperty("User", out JsonElement userElem) ? userElem.GetString() ?? string.Empty : string.Empty;
            if (!allowRootUser && (string.IsNullOrWhiteSpace(user) || user == "0" || user == "root" || user.StartsWith("0:", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"User check failed: container is running as root ('{user}') without compatibility profile.");
            }

            // 1e. Network
            if (hostConfig.TryGetProperty("NetworkMode", out JsonElement netModeElem))
            {
                string? netMode = netModeElem.GetString();
                if (netMode is not null
                    && !netMode.StartsWith("kautuka-workload", StringComparison.OrdinalIgnoreCase)
                    && !netMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Network check failed: unexpected NetworkMode '{netMode}'.");
                }
            }

            if (container.TryGetProperty("NetworkSettings", out JsonElement netSettings))
            {
                if (netSettings.TryGetProperty("Networks", out JsonElement networks) && networks.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty networkProp in networks.EnumerateObject())
                    {
                        if (!networkProp.Name.Equals("kautuka-workload", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"Network check failed: container is attached to extra network '{networkProp.Name}'.");
                        }

                        if (networkProp.Value.TryGetProperty("IPAddress", out JsonElement ipElem))
                        {
                            string? actualIp = ipElem.GetString();
                            if (!string.Equals(actualIp, allocatedIp, StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException($"Network check failed: IP '{actualIp}' does not match allocated IP '{allocatedIp}'.");
                            }
                        }

                        if (networkProp.Value.TryGetProperty("GlobalIPv6Address", out JsonElement ipv6Elem))
                        {
                            string? ipv6 = ipv6Elem.GetString();
                            if (!string.IsNullOrWhiteSpace(ipv6))
                            {
                                throw new InvalidOperationException($"Network check failed: unexpected IPv6 address '{ipv6}'.");
                            }
                        }
                    }
                }
            }

            // 1f. Namespaces
            string pidMode = hostConfig.TryGetProperty("PidMode", out JsonElement pidModeElem) ? pidModeElem.GetString() ?? string.Empty : string.Empty;
            string ipcMode = hostConfig.TryGetProperty("IpcMode", out JsonElement ipcModeElem) ? ipcModeElem.GetString() ?? string.Empty : string.Empty;
            string utsMode = hostConfig.TryGetProperty("UtsMode", out JsonElement utsModeElem) ? utsModeElem.GetString() ?? string.Empty : string.Empty;
            string cgroupMode = hostConfig.TryGetProperty("CgroupMode", out JsonElement cgElem) ? cgElem.GetString() ?? string.Empty : string.Empty;

            if (pidMode.Equals("host", StringComparison.OrdinalIgnoreCase)
                || ipcMode.Equals("host", StringComparison.OrdinalIgnoreCase)
                || utsMode.Equals("host", StringComparison.OrdinalIgnoreCase)
                || cgroupMode.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Namespace check failed: host namespace sharing is enabled.");
            }

            // 1g. Devices
            if (hostConfig.TryGetProperty("Devices", out JsonElement devElem) && devElem.ValueKind == JsonValueKind.Array && devElem.GetArrayLength() > 0)
            {
                throw new InvalidOperationException("Device check failed: container has host devices attached.");
            }

            // 1h. Mounts
            if (container.TryGetProperty("Mounts", out JsonElement mountsElem) && mountsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement mount in mountsElem.EnumerateArray())
                {
                    string source = mount.TryGetProperty("Source", out JsonElement srcElem) ? srcElem.GetString() ?? string.Empty : string.Empty;
                    string dest = mount.TryGetProperty("Destination", out JsonElement dstElem) ? dstElem.GetString() ?? string.Empty : string.Empty;

                    if (source.Contains("docker.sock", StringComparison.OrdinalIgnoreCase)
                        || source.Contains("podman.sock", StringComparison.OrdinalIgnoreCase)
                        || source.Equals("/proc", StringComparison.OrdinalIgnoreCase)
                        || source.StartsWith("/proc/", StringComparison.OrdinalIgnoreCase)
                        || source.Equals("/sys", StringComparison.OrdinalIgnoreCase)
                        || source.StartsWith("/sys/", StringComparison.OrdinalIgnoreCase)
                        || source.Equals("/dev", StringComparison.OrdinalIgnoreCase)
                        || source.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase)
                        || source.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Mount check failed: prohibited mount source '{source}'.");
                    }
                }
            }

            // 2. In-netns ruleset verification (when on Linux)
            if (OperatingSystem.IsLinux())
            {
                if (pid <= 0)
                {
                    throw new InvalidOperationException($"Process check failed: invalid or non-positive PID ({pid}) on Linux.");
                }

                await VerifyNetnsRulesetAsync(pid, allocatedIp, cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            return new WorkloadVerificationResult(containerId, allocatedIp, true, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            long exposureMs = stopwatch.ElapsedMilliseconds;

            // Kill and force remove immediately on verification failure:
            try
            {
                await podmanRunner.RunAsync(["kill", containerId], 5, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore kill errors during cleanup
            }

            try
            {
                await podmanRunner.RunAsync(["rm", "-f", containerId], 5, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore rm errors during cleanup
            }

            // Deregister if registry provided:
            registry?.Deregister(allocatedIp, containerId);

            throw new InvalidOperationException(
                $"WorkloadPostStartVerifier failed for '{containerId}' at '{allocatedIp}' after {exposureMs} ms: {ex.Message}",
                ex);
        }
    }

    private static async Task VerifyNetnsRulesetAsync(
        int pid,
        string allocatedIp,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "nsenter",
                Arguments = $"-t {pid} -n nft -j list table inet {WorkloadRulesetRenderer.WorkloadTableName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        string stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string stdErr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"nsenter nft -j list table failed (rc {process.ExitCode}): {stdErr}");
        }

        string liveHash = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(
            stdOut,
            WorkloadRulesetRenderer.RequiredWorkloadComments,
            allocatedIp);

        string expectedHash = WorkloadRulesetRenderer.ComputeExpectedCanonicalRulesetHash(allocatedIp);
        if (!string.Equals(liveHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"In-netns ruleset verification failed: live hash '{liveHash}' does not match expected canonical golden hash '{expectedHash}'.");
        }
    }
}
