using System.Globalization;
using System.Net;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

public sealed record WorkloadMount(
    string HostPath,
    string ContainerPath,
    bool ReadOnly = false)
{
    public override string ToString()
        => ReadOnly ? $"{HostPath}:{ContainerPath}:ro" : $"{HostPath}:{ContainerPath}";
}

/// <summary>
/// Immutable allowlist spec builder for workload containers on <c>kautuka-workload</c> (Section 4.3.4 item 1).
/// Enforces strict containment and identity labels, and rejects any prohibited capability,
/// device, mount, host namespace, network flag, or unapproved relaxation.
/// </summary>
public sealed record WorkloadContainerSpec
{
    public const string DefaultNetworkName = "kautuka-workload";
    public const string DefaultDnsAddress = "10.247.0.2";
    public const string DefaultUser = "65532:65532";
    public const string DefaultMemoryLimit = "536870912";
    public const int DefaultPidsLimit = 256;
    public const string WorkloadRoleAnnotation = "io.kautuka.role=workload";
    public const string Ipv6DisableSysctl = "net.ipv6.conf.all.disable_ipv6=1";

    public static readonly IReadOnlySet<string> AllowedFileOwnershipCaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CHOWN", "DAC_OVERRIDE", "FOWNER", "SETUID", "SETGID", "FSETID",
        "CAP_CHOWN", "CAP_DAC_OVERRIDE", "CAP_FOWNER", "CAP_SETUID", "CAP_SETGID", "CAP_FSETID"
    };

    public string ContainerName { get; init; }
    public string Image { get; init; }
    public string AllocatedIp { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Environment { get; init; } = [];
    public IReadOnlyList<WorkloadMount> Mounts { get; init; } = [];
    public IReadOnlyList<string> AllowedMountRoots { get; init; } = [];
    public IReadOnlyList<string> TmpfsMounts { get; init; } = ["/tmp:rw,noexec,nosuid"];
    public IReadOnlyList<string> AddHosts { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public bool Persistent { get; init; } = true;
    public bool Replace { get; init; }
    public double CpuLimit { get; init; }
    public string? MemoryLimit { get; init; }
    public int? PidsLimit { get; init; }
    public string? Purpose { get; init; }

    // Identity labels:
    public string UserId { get; init; } = string.Empty;
    public string AgentId { get; init; } = string.Empty;
    public string ConnectorId { get; init; } = string.Empty;
    public string ExecContext { get; init; } = "isolated";
    public string Kind { get; init; } = "connector";
    public bool Systemwide { get; init; }
    public string Egress { get; init; } = "normal";

    // Manager labels:
    public long DesiredStateVersion { get; init; } = 1;
    public string SpecFingerprint { get; init; } = string.Empty;
    public string ManagerRole { get; init; } = "connector";

    // Gates and profiles:
    public bool StartupGateOpen { get; init; } = true;
    public bool IsBraked { get; init; }
    public ImageCompatProfile? CompatibilityProfile { get; init; }

    public WorkloadContainerSpec(
        string containerName,
        string image,
        string allocatedIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(allocatedIp);

        ContainerName = containerName.Trim();
        Image = image.Trim();
        AllocatedIp = ValidateAndNormalizeAllocatedIp(allocatedIp);
    }

    public static string ValidateAndNormalizeAllocatedIp(string ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        string trimmed = ip.Trim();
        if (!IPAddress.TryParse(trimmed, out IPAddress? parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"Invalid IPv4 address '{ip}'.", nameof(ip));
        }

        byte[] bytes = parsed.GetAddressBytes();
        if (bytes[0] != 10 || bytes[1] != 247)
        {
            throw new ArgumentException($"IP '{ip}' is outside the kautuka-workload 10.247.0.0/16 network.", nameof(ip));
        }

        if (bytes[2] == 0)
        {
            throw new ArgumentException($"IP '{ip}' is in reserved infra range 10.247.0.0/24.", nameof(ip));
        }

        if (bytes[3] == 0 || bytes[3] == 255)
        {
            throw new ArgumentException($"IP '{ip}' has prohibited host octet {bytes[3]}.", nameof(ip));
        }

        return trimmed;
    }

    /// <summary>
    /// Validates callers' parameters against forbidden flags, mounts, and identity rules,
    /// then renders the argument list for <c>podman run</c>.
    /// </summary>
    public IReadOnlyList<string> Render()
    {
        if (!StartupGateOpen)
        {
            throw new InvalidOperationException("Creation rejected: Startup gate is closed (network protection is not ready).");
        }

        if (IsBraked)
        {
            throw new InvalidOperationException("Creation rejected: Network paused (emergency brake active).");
        }

        ValidateIdentity();
        ValidateMounts();

        ContainerImageProvenancePolicy.EnsureTrusted(Image, $"Workload container '{ContainerName}'");

        foreach (KeyValuePair<string, string> env in Environment)
        {
            if (env.Key.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase)
                || env.Key.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase)
                || env.Key.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase)
                || env.Key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Creation rejected: proxy environment variable '{env.Key}' is prohibited.", nameof(Environment));
            }
        }

        if (CompatibilityProfile is not null)
        {
            foreach (string relaxation in CompatibilityProfile.Relaxations)
            {
                if (!ImageCompatibilityStore.AllowedRelaxations.Contains(relaxation))
                {
                    throw new ArgumentException($"Creation rejected: unapproved compatibility relaxation '{relaxation}'.", nameof(CompatibilityProfile));
                }
            }
        }

        bool allowWritableRootfs = CompatibilityProfile?.Relaxations.Contains(ImageCompatibilityStore.WritableRootfs) == true;
        bool allowRootUser = CompatibilityProfile?.Relaxations.Contains(ImageCompatibilityStore.RootUser) == true;
        bool allowFileOwnershipCaps = CompatibilityProfile?.Relaxations.Contains(ImageCompatibilityStore.FileOwnershipCaps) == true;

        string effectiveUser = allowRootUser ? "0:0" : DefaultUser;
        string compatLabel = CompatibilityProfile is not null
            ? string.Join(',', CompatibilityProfile.Relaxations)
            : string.Empty;

        var args = new List<string> { "run" };
        if (Replace)
        {
            args.Add("--replace");
        }

        if (Persistent)
        {
            args.Add("-d");
        }
        else
        {
            args.AddRange(["--rm", "-i"]);
        }

        args.AddRange([
            "--name", ContainerName,
            "--user", effectiveUser,
            "--cap-drop", "ALL",
        ]);

        if (allowFileOwnershipCaps)
        {
            args.AddRange([
                "--cap-add", "CHOWN",
                "--cap-add", "DAC_OVERRIDE",
                "--cap-add", "FOWNER",
                "--cap-add", "SETUID",
                "--cap-add", "SETGID",
                "--cap-add", "FSETID",
            ]);
        }

        args.AddRange([
            "--security-opt", "no-new-privileges",
            "--pids-limit", (PidsLimit ?? DefaultPidsLimit).ToString(CultureInfo.InvariantCulture),
            "--memory", MemoryLimit ?? DefaultMemoryLimit,
        ]);

        if (CpuLimit > 0)
        {
            args.AddRange(["--cpus", CpuLimit.ToString(CultureInfo.InvariantCulture)]);
        }

        if (!allowWritableRootfs)
        {
            args.Add("--read-only");
        }

        foreach (string tmpfs in TmpfsMounts)
        {
            args.AddRange(["--tmpfs", tmpfs]);
        }

        args.AddRange([
            "--network", $"{DefaultNetworkName}:ip={AllocatedIp}",
            "--dns", DefaultDnsAddress,
            "--sysctl", Ipv6DisableSysctl,
            "--annotation", WorkloadRoleAnnotation,
            "--label", $"io.kautuka.user={UserId ?? string.Empty}",
            "--label", $"io.kautuka.connector={ConnectorId ?? string.Empty}",
            "--label", $"io.kautuka.exec-context={ExecContext ?? string.Empty}",
            "--label", $"io.kautuka.kind={Kind ?? string.Empty}",
            "--label", $"io.kautuka.systemwide={(Systemwide ? "true" : "false")}",
            "--label", $"io.kautuka.egress={Egress ?? string.Empty}",
            "--label", $"io.kautuka.compat={compatLabel}",
        ]);

        if (!string.IsNullOrWhiteSpace(AgentId))
        {
            args.AddRange(["--label", $"io.kautuka.agent={AgentId}"]);
        }

        if (!string.IsNullOrWhiteSpace(Purpose))
        {
            args.AddRange(["--label", $"io.kautuka.purpose={Purpose}"]);
        }

        args.AddRange([
            "--label", "kautuka.managed=manager",
            "--label", $"kautuka.role={ManagerRole}",
            "--label", $"kautuka.desired-version={DesiredStateVersion.ToString(CultureInfo.InvariantCulture)}",
            "--label", $"kautuka.spec-fingerprint={SpecFingerprint}",
        ]);

        if (!string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            args.AddRange(["--workdir", WorkingDirectory]);
        }

        foreach (string host in AddHosts)
        {
            if (!string.Equals(host, "kautuka-pkgs-proxy:10.247.0.3", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Creation rejected: add-host entry '{host}' is not an approved infrastructure mapping.", nameof(AddHosts));
            }

            args.AddRange(["--add-host", host]);
        }

        foreach (WorkloadMount mount in Mounts)
        {
            args.AddRange(["--volume", mount.ToString()]);
        }

        foreach (KeyValuePair<string, string> env in Environment)
        {
            if (string.IsNullOrEmpty(env.Value))
            {
                args.AddRange(["--env", env.Key]);
            }
            else
            {
                args.AddRange(["--env", $"{env.Key}={env.Value}"]);
            }
        }

        args.Add(Image);

        if (Persistent)
        {
            args.AddRange(["sh", "-lc", "tail -f /dev/null"]);
        }
        else if (!string.IsNullOrWhiteSpace(Command))
        {
            args.Add(Command);
            args.AddRange(Args);
        }
        else if (Args.Count > 0)
        {
            args.AddRange(Args);
        }

        return args;
    }

    private void ValidateIdentity()
    {
        if (Kind is not ("agent-sandbox" or "connector" or "builder" or "system"))
        {
            throw new ArgumentException($"Unsupported workload kind '{Kind}'.", nameof(Kind));
        }

        if (ExecContext is not ("isolated" or "isolated-with-agent-files" or "agent-sandbox" or "builder" or "system"))
        {
            throw new ArgumentException($"Unsupported exec context '{ExecContext}'.", nameof(ExecContext));
        }

        if (Egress is not ("normal" or "deny-all" or "proxy-only"))
        {
            throw new ArgumentException($"Unsupported egress mode '{Egress}'.", nameof(Egress));
        }

        if (UserId == "-" || AgentId == "-")
        {
            throw new ArgumentException("Placeholder '-' identities are not allowed.", nameof(UserId));
        }

        if (Egress is "normal")
        {
            if (Systemwide)
            {
                if (string.IsNullOrWhiteSpace(ConnectorId))
                {
                    throw new ArgumentException("Systemwide connector requires a connectorId.", nameof(ConnectorId));
                }
            }
            else if (Kind is "connector" or "agent-sandbox")
            {
                if (string.IsNullOrWhiteSpace(UserId))
                {
                    throw new ArgumentException("Ordinary workload requires a userId.", nameof(UserId));
                }

                if (!string.Equals(Purpose, "probe", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(AgentId))
                    {
                        throw new ArgumentException("Ordinary non-probe workload requires an agentId.", nameof(AgentId));
                    }
                }
            }
        }
    }

    private void ValidateMounts()
    {
        foreach (WorkloadMount mount in Mounts)
        {
            string host = mount.HostPath.Replace('\\', '/');
            string container = mount.ContainerPath.Replace('\\', '/');

            if (AllowedMountRoots.Count == 0 || !IsWithinAllowedMountRoot(host))
            {
                throw new ArgumentException($"Creation rejected: mount source '{mount.HostPath}' is outside the workspace mount allowlist.", nameof(Mounts));
            }

            if (host.Contains("docker.sock", StringComparison.OrdinalIgnoreCase) || host.Contains("podman.sock", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Creation rejected: mounting Docker or Podman sockets is prohibited.", nameof(Mounts));
            }

            if (host.Equals("/proc", StringComparison.OrdinalIgnoreCase) || host.StartsWith("/proc/", StringComparison.OrdinalIgnoreCase)
                || container.Equals("/proc", StringComparison.OrdinalIgnoreCase) || container.StartsWith("/proc/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Creation rejected: mounting /proc is prohibited.", nameof(Mounts));
            }

            if (host.Equals("/sys", StringComparison.OrdinalIgnoreCase) || host.StartsWith("/sys/", StringComparison.OrdinalIgnoreCase)
                || container.Equals("/sys", StringComparison.OrdinalIgnoreCase) || container.StartsWith("/sys/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Creation rejected: mounting /sys is prohibited.", nameof(Mounts));
            }

            if (host.Equals("/dev", StringComparison.OrdinalIgnoreCase) || host.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase)
                || container.Equals("/dev", StringComparison.OrdinalIgnoreCase) || container.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Creation rejected: mounting /dev is prohibited.", nameof(Mounts));
            }

            if (host.EndsWith(".key", StringComparison.OrdinalIgnoreCase) || host.Contains("/.key", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Creation rejected: mounting private key paths is prohibited.", nameof(Mounts));
            }
        }
    }

    private bool IsWithinAllowedMountRoot(string hostPath)
    {
        string fullHostPath = Path.GetFullPath(hostPath);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string allowedRoot in AllowedMountRoots)
        {
            if (string.IsNullOrWhiteSpace(allowedRoot))
            {
                continue;
            }

            string fullRoot = Path.GetFullPath(allowedRoot);
            if (string.Equals(fullHostPath, fullRoot, comparison))
            {
                return true;
            }

            string relative = Path.GetRelativePath(fullRoot, fullHostPath);
            if (!Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", comparison)
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)
                && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, comparison))
            {
                return true;
            }
        }

        return false;
    }
}
