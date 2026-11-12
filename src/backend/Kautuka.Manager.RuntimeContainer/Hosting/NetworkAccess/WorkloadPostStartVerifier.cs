using System.Diagnostics;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Sandbox;
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
    private readonly WorkspaceDesiredStateStore? desiredStateStore;

    public WorkloadPostStartVerifier(PodmanCommandRunner podmanRunner, WorkspaceDesiredStateStore? desiredStateStore = null)
    {
        ArgumentNullException.ThrowIfNull(podmanRunner);
        this.podmanRunner = podmanRunner;
        this.desiredStateStore = desiredStateStore;
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
        string registeredId = containerId;
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

            if (container.TryGetProperty("Id", out JsonElement inspectedIdElem))
            {
                string? inspectedId = inspectedIdElem.GetString();
                if (!string.IsNullOrWhiteSpace(inspectedId)
                    && registry?.UpdateContainerId(allocatedIp, containerId, inspectedId) == true)
                {
                    registeredId = inspectedId;
                }
            }

            JsonElement hostConfig = container.GetProperty("HostConfig");
            JsonElement config = container.GetProperty("Config");
            JsonElement state = container.GetProperty("State");

            // Verify the runtime view of the typed spec's process and kernel hardening.
            bool noNewPrivileges = hostConfig.TryGetProperty("SecurityOpt", out JsonElement securityOpt)
                && securityOpt.ValueKind == JsonValueKind.Array
                && securityOpt.EnumerateArray().Any(static option =>
                    string.Equals(option.GetString(), "no-new-privileges", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(option.GetString(), "no-new-privileges:true", StringComparison.OrdinalIgnoreCase));
            if (!noNewPrivileges)
            {
                throw new InvalidOperationException("Security check failed: no-new-privileges is not enabled.");
            }

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

            IReadOnlySet<string> allowedCapabilities = allowFileCaps
                ? WorkloadContainerSpec.AllowedFileOwnershipCaps
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!LiveContainerSpec.CapDropIncludesAll(container, hostConfig, allowedCapabilities))
            {
                throw new InvalidOperationException("Capability check failed: CapDrop does not remove all prohibited capabilities.");
            }

            VerifyCapabilitySet(container, "EffectiveCaps", allowedCapabilities);
            VerifyCapabilitySet(container, "BoundingCaps", allowedCapabilities);

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
            if (!container.TryGetProperty("Mounts", out JsonElement mountsElem) || mountsElem.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Mount check failed: Podman inspect did not return a mount list.");
            }

            RegistryEntry? workloadIdentity = registry?.FindByIp(allocatedIp);
            IReadOnlyList<string> allowedMountRoots = ResolveAllowedWorkspaceRoots(workloadIdentity);
            foreach (JsonElement mount in mountsElem.EnumerateArray())
            {
                string mountType = mount.TryGetProperty("Type", out JsonElement typeElem) && typeElem.ValueKind == JsonValueKind.String
                    ? typeElem.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(mountType))
                {
                    throw new InvalidOperationException("Mount check failed: a mount is missing its type.");
                }

                string source = mount.TryGetProperty("Source", out JsonElement srcElem) ? srcElem.GetString() ?? string.Empty : string.Empty;
                string dest = mount.TryGetProperty("Destination", out JsonElement dstElem) ? dstElem.GetString() ?? string.Empty : string.Empty;

                if (!mountType.Equals("tmpfs", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(source) || !IsWithinAllowedRoot(source, allowedMountRoots)))
                {
                    throw new InvalidOperationException($"Mount check failed: {mountType} source '{source}' is outside the configured workspace roots.");
                }

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
                    throw new InvalidOperationException($"Mount check failed: prohibited mount source '{source}' at '{dest}'.");
                }
            }

            // 2. In-netns ruleset verification (when on Linux)
            if (OperatingSystem.IsLinux())
            {
                if (pid <= 0)
                {
                    throw new InvalidOperationException($"Process check failed: invalid or non-positive PID ({pid}) on Linux.");
                }

                await VerifyNetnsIpv6DisableAsync(pid, cancellationToken).ConfigureAwait(false);
                await VerifyNetnsRulesetAsync(pid, allocatedIp, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                VerifyInspectIpv6Disable(hostConfig);
            }

            stopwatch.Stop();
            return new WorkloadVerificationResult(registeredId, allocatedIp, true, stopwatch.ElapsedMilliseconds);
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
            if (registry is not null && !registry.Deregister(allocatedIp, registeredId))
            {
                registry.Deregister(allocatedIp, containerId);
            }

            throw new InvalidOperationException(
                $"WorkloadPostStartVerifier failed for '{containerId}' at '{allocatedIp}' after {exposureMs} ms: {ex.Message}",
                ex);
        }
    }

    private IReadOnlyList<string> ResolveAllowedWorkspaceRoots(RegistryEntry? workloadIdentity)
    {
        if (workloadIdentity is null
            || string.IsNullOrWhiteSpace(workloadIdentity.UserId)
            || string.IsNullOrWhiteSpace(workloadIdentity.AgentId)
            || desiredStateStore is null)
        {
            return [];
        }

        return [.. desiredStateStore.Snapshot.DesiredWorkspaces
            .Where(context => string.Equals(context.ScopeType, "agent", StringComparison.OrdinalIgnoreCase)
                && string.Equals(context.ScopeId, workloadIdentity.AgentId, StringComparison.Ordinal)
                && string.Equals(context.UserId, workloadIdentity.UserId, StringComparison.Ordinal))
            .Select(static context => context.HostWorkspacePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Select(WorkspaceAuthorityPolicy.CanonicalizeRoot)];
    }

    private static void VerifyCapabilitySet(JsonElement container, string propertyName, IReadOnlySet<string> allowed)
    {
        if (!container.TryGetProperty(propertyName, out JsonElement capabilities)
            || (capabilities.ValueKind != JsonValueKind.Array
                && !(capabilities.ValueKind == JsonValueKind.Null && allowed.Count == 0)))
        {
            throw new InvalidOperationException($"Capability check failed: {propertyName} is missing or null.");
        }

        if (capabilities.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        foreach (JsonElement capability in capabilities.EnumerateArray())
        {
            string? name = capability.ValueKind == JsonValueKind.String ? capability.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || !allowed.Contains(name))
            {
                throw new InvalidOperationException($"Capability check failed: {propertyName} contains a prohibited or invalid capability.");
            }
        }
    }

    private static bool IsWithinAllowedRoot(string path, IReadOnlyList<string> allowedRoots)
    {
        string fullPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string fullRoot = Path.GetFullPath(root);
            string relative = Path.GetRelativePath(fullRoot, fullPath);
            if (string.Equals(relative, ".", comparison)
                || (!Path.IsPathRooted(relative)
                    && !string.Equals(relative, "..", comparison)
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)
                    && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, comparison)))
            {
                return true;
            }
        }

        return false;
    }

    private static void VerifyInspectIpv6Disable(JsonElement hostConfig)
    {
        if (!hostConfig.TryGetProperty("Sysctls", out JsonElement sysctls)
            || sysctls.ValueKind != JsonValueKind.Object
            || !sysctls.TryGetProperty("net.ipv6.conf.all.disable_ipv6", out JsonElement disableIpv6)
            || !string.Equals(disableIpv6.GetString(), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Network check failed: IPv6 disable sysctl is missing or incorrect.");
        }
    }

    private async Task VerifyNetnsIpv6DisableAsync(int pid, CancellationToken cancellationToken)
    {
        string pidText = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const string sysctlPath = "/proc/sys/net/ipv6/conf/all/disable_ipv6";
        string actualValue;
        if (!Environment.IsPrivilegedProcess)
        {
            PodmanCommandResult result = await podmanRunner
                .RunAsync(["unshare", "nsenter", "-t", pidText, "-n", "cat", sysctlPath], 10, cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"podman unshare nsenter could not read the IPv6 disable sysctl (rc {result.ExitCode}): {result.StdErr}");
            }

            actualValue = result.StdOut.Trim();
        }
        else
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nsenter",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            foreach (string argument in new[] { "-t", pidText, "-n", "cat", sysctlPath })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            actualValue = (await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
            string standardError = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"nsenter could not read the IPv6 disable sysctl (rc {process.ExitCode}): {standardError}");
            }
        }

        if (!string.Equals(actualValue, "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Network check failed: IPv6 disable sysctl is '{actualValue}' in the workload network namespace.");
        }
    }

    internal async Task VerifyNetnsRulesetAsync(
        int pid,
        string allocatedIp,
        CancellationToken cancellationToken)
    {
        string pidText = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string stdOut;
        if (!Environment.IsPrivilegedProcess)
        {
            // Rootless Podman places container netns inside the user namespace; direct nsenter fails with EPERM.
            PodmanCommandResult nftResult = await podmanRunner
                .RunAsync(
                    ["unshare", "nsenter", "-t", pidText, "-n", "nft", "-j", "list", "table", "inet", WorkloadRulesetRenderer.WorkloadTableName],
                    10,
                    cancellationToken)
                .ConfigureAwait(false);
            if (nftResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"podman unshare nsenter nft -j list table failed (rc {nftResult.ExitCode}): {nftResult.StdErr}");
            }

            stdOut = nftResult.StdOut;
        }
        else
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nsenter",
                    Arguments = $"-t {pidText} -n nft -j list table inet {WorkloadRulesetRenderer.WorkloadTableName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string stdErr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"nsenter nft -j list table failed (rc {process.ExitCode}): {stdErr}");
            }
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
