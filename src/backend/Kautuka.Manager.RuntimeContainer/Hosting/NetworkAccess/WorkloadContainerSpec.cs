using System.Globalization;
using System.Net;
using Kautuka.NetworkAccessController;

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
    public IReadOnlyList<string> TmpfsMounts { get; init; } = ["/tmp:rw,noexec,nosuid"];
    public IReadOnlyList<string> AddHosts { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public bool Persistent { get; init; } = true;

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

        bool allowWritableRootfs = CompatibilityProfile?.Relaxations.Contains(ImageCompatibilityStore.WritableRootfs) == true;
        bool allowRootUser = CompatibilityProfile?.Relaxations.Contains(ImageCompatibilityStore.RootUser) == true;
        bool allowFileOwnershipCaps = CompatibilityProfile?.Relaxations.Contains(ImageCompatibilityStore.FileOwnershipCaps) == true;

        string effectiveUser = allowRootUser ? "0:0" : DefaultUser;
        string compatLabel = CompatibilityProfile is not null
            ? string.Join(',', CompatibilityProfile.Relaxations)
            : string.Empty;

        var args = new List<string> { "run" };
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
            "--pids-limit", DefaultPidsLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", DefaultMemoryLimit,
        ]);

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
            "--label", $"io.kautuka.agent={AgentId ?? string.Empty}",
            "--label", $"io.kautuka.connector={ConnectorId ?? string.Empty}",
            "--label", $"io.kautuka.exec-context={ExecContext ?? string.Empty}",
            "--label", $"io.kautuka.kind={Kind ?? string.Empty}",
            "--label", $"io.kautuka.systemwide={(Systemwide ? "true" : "false")}",
            "--label", $"io.kautuka.egress={Egress ?? string.Empty}",
            "--label", $"io.kautuka.compat={compatLabel}",
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
            args.AddRange(["--add-host", host]);
        }

        foreach (WorkloadMount mount in Mounts)
        {
            args.AddRange(["--volume", mount.ToString()]);
        }

        foreach (KeyValuePair<string, string> env in Environment)
        {
            args.AddRange(["--env", $"{env.Key}={env.Value}"]);
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

    /// <summary>
    /// Validates raw arguments or specifications from external sources, ensuring no forbidden
    /// flags or settings bypass container containment.
    /// </summary>
    public static void AssertAllowedOptions(
        IReadOnlyList<string>? flags,
        IReadOnlyDictionary<string, string>? labels,
        IReadOnlyDictionary<string, string>? annotations,
        ImageCompatProfile? compatProfile = null)
    {
        if (flags is not null)
        {
            for (int i = 0; i < flags.Count; i++)
            {
                string flag = flags[i];
                if (flag.Equals("--privileged", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: '--privileged' flag is prohibited.", nameof(flags));
                }

                if (flag.StartsWith("--privileged=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: '--privileged' flag is prohibited.", nameof(flags));
                }

                if (flag.Equals("--device", StringComparison.OrdinalIgnoreCase) || flag.StartsWith("--device=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: '--device' flag is prohibited.", nameof(flags));
                }

                if (flag.Equals("--userns", StringComparison.OrdinalIgnoreCase) || flag.StartsWith("--userns=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: '--userns' flag is prohibited.", nameof(flags));
                }

                if (flag.Equals("--hooks-dir", StringComparison.OrdinalIgnoreCase) || flag.StartsWith("--hooks-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: '--hooks-dir' flag is prohibited.", nameof(flags));
                }

                if (flag.Equals("--runtime", StringComparison.OrdinalIgnoreCase) || flag.StartsWith("--runtime=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: '--runtime' flag is prohibited.", nameof(flags));
                }

                if (flag.Equals("--volumes-from", StringComparison.OrdinalIgnoreCase) || flag.StartsWith("--volumes-from=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: '--volumes-from' flag is prohibited.", nameof(flags));
                }

                if (flag.Equals("--pod", StringComparison.OrdinalIgnoreCase) || flag.StartsWith("--pod=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: '--pod' flag is prohibited.", nameof(flags));
                }

                if (flag.StartsWith("--infra", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: infra flags are prohibited.", nameof(flags));
                }

                if (flag.StartsWith("--init-ctr", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: '--init-ctr' flag is prohibited.", nameof(flags));
                }

                if (flag.Equals("--ip", StringComparison.OrdinalIgnoreCase) || flag.StartsWith("--ip=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: caller cannot specify '--ip'; IP is chosen by Manager.", nameof(flags));
                }

                if (flag.Equals("--mac-address", StringComparison.OrdinalIgnoreCase) || flag.StartsWith("--mac-address=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Creation rejected: caller cannot specify '--mac-address'.", nameof(flags));
                }

                if (flag.Equals("--network", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Count)
                {
                    string networkArg = flags[i + 1];
                    if (!networkArg.StartsWith(DefaultNetworkName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException($"Creation rejected: network '{networkArg}' is not permitted; only '{DefaultNetworkName}' is allowed.", nameof(flags));
                    }
                }
                else if (flag.StartsWith("--network=", StringComparison.OrdinalIgnoreCase))
                {
                    string networkArg = flag["--network=".Length..];
                    if (!networkArg.StartsWith(DefaultNetworkName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException($"Creation rejected: network '{networkArg}' is not permitted; only '{DefaultNetworkName}' is allowed.", nameof(flags));
                    }
                }

                if (flag.Equals("--cap-add", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Count)
                {
                    string cap = flags[i + 1];
                    ValidateCapAdd(cap, compatProfile, nameof(flags));
                }
                else if (flag.StartsWith("--cap-add=", StringComparison.OrdinalIgnoreCase))
                {
                    string cap = flag["--cap-add=".Length..];
                    ValidateCapAdd(cap, compatProfile, nameof(flags));
                }

                if (flag.Equals("--security-opt", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Count)
                {
                    string opt = flags[i + 1];
                    ValidateSecurityOpt(opt, nameof(flags));
                }
                else if (flag.StartsWith("--security-opt=", StringComparison.OrdinalIgnoreCase))
                {
                    string opt = flag["--security-opt=".Length..];
                    ValidateSecurityOpt(opt, nameof(flags));
                }

                if (flag.StartsWith("--pid=", StringComparison.OrdinalIgnoreCase) || (flag.Equals("--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Count))
                {
                    string val = flag.StartsWith("--pid=", StringComparison.OrdinalIgnoreCase) ? flag["--pid=".Length..] : flags[i + 1];
                    if (val.Equals("host", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("Creation rejected: '--pid=host' is prohibited.", nameof(flags));
                    }
                }

                if (flag.StartsWith("--ipc=", StringComparison.OrdinalIgnoreCase) || (flag.Equals("--ipc", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Count))
                {
                    string val = flag.StartsWith("--ipc=", StringComparison.OrdinalIgnoreCase) ? flag["--ipc=".Length..] : flags[i + 1];
                    if (val.Equals("host", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("Creation rejected: '--ipc=host' is prohibited.", nameof(flags));
                    }
                }

                if (flag.StartsWith("--uts=", StringComparison.OrdinalIgnoreCase) || (flag.Equals("--uts", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Count))
                {
                    string val = flag.StartsWith("--uts=", StringComparison.OrdinalIgnoreCase) ? flag["--uts=".Length..] : flags[i + 1];
                    if (val.Equals("host", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("Creation rejected: '--uts=host' is prohibited.", nameof(flags));
                    }
                }

                if (flag.StartsWith("--cgroupns=", StringComparison.OrdinalIgnoreCase) || (flag.Equals("--cgroupns", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Count))
                {
                    string val = flag.StartsWith("--cgroupns=", StringComparison.OrdinalIgnoreCase) ? flag["--cgroupns=".Length..] : flags[i + 1];
                    if (val.Equals("host", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("Creation rejected: '--cgroupns=host' is prohibited.", nameof(flags));
                    }
                }

                if (flag.StartsWith("--sysctl=", StringComparison.OrdinalIgnoreCase) || (flag.Equals("--sysctl", StringComparison.OrdinalIgnoreCase) && i + 1 < flags.Count))
                {
                    string val = flag.StartsWith("--sysctl=", StringComparison.OrdinalIgnoreCase) ? flag["--sysctl=".Length..] : flags[i + 1];
                    if (!val.Equals("net.ipv6.conf.all.disable_ipv6=1", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException($"Creation rejected: sysctl '{val}' is prohibited.", nameof(flags));
                    }
                }
            }
        }

        if (labels is not null)
        {
            foreach (string labelKey in labels.Keys)
            {
                if (labelKey.StartsWith("io.kautuka.", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Creation rejected: caller cannot specify reserved label '{labelKey}'.", nameof(labels));
                }

                if (labelKey.StartsWith("run.oci.", StringComparison.OrdinalIgnoreCase) || labelKey.StartsWith("io.podman.", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Creation rejected: caller cannot specify OCI/Podman label '{labelKey}'.", nameof(labels));
                }
            }
        }

        if (annotations is not null)
        {
            foreach (string annoKey in annotations.Keys)
            {
                if (annoKey.StartsWith("io.kautuka.", StringComparison.OrdinalIgnoreCase) && !annoKey.Equals("io.kautuka.role", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Creation rejected: caller cannot specify reserved annotation '{annoKey}'.", nameof(annotations));
                }

                if (annoKey.StartsWith("run.oci.", StringComparison.OrdinalIgnoreCase) || annoKey.StartsWith("io.podman.", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Creation rejected: caller cannot specify OCI/Podman annotation '{annoKey}'.", nameof(annotations));
                }
            }
        }
    }

    private static void ValidateCapAdd(string cap, ImageCompatProfile? compatProfile, string paramName)
    {
        bool hasFileCapsCompat = compatProfile?.Relaxations.Contains(ImageCompatibilityStore.FileOwnershipCaps) == true;
        if (!hasFileCapsCompat)
        {
            throw new ArgumentException($"Creation rejected: '--cap-add {cap}' is prohibited.", paramName);
        }

        if (!AllowedFileOwnershipCaps.Contains(cap))
        {
            throw new ArgumentException($"Creation rejected: capability '{cap}' is prohibited even under compatibility profile.", paramName);
        }
    }

    private static void ValidateSecurityOpt(string opt, string paramName)
    {
        if (!opt.Equals("no-new-privileges", StringComparison.OrdinalIgnoreCase)
            && !opt.Equals("no-new-privileges=true", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Creation rejected: security-opt '{opt}' is prohibited.", paramName);
        }
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

                // If not a user-level probe, require agentId
                if (ExecContext is "isolated" or "isolated-with-agent-files" or "agent-sandbox")
                {
                    if (string.IsNullOrWhiteSpace(AgentId))
                    {
                        throw new ArgumentException("Ordinary agent workload requires an agentId.", nameof(AgentId));
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
}
