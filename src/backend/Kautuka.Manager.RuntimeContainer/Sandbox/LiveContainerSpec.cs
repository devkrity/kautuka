using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.RuntimeContainer.Sandbox;

internal sealed record LiveContainerExpectation(
    string NetworkName,
    string? HostMountPath,
    string ContainerMountPath,
    bool MountReadOnly,
    long PidsLimit,
    IReadOnlyList<string> RequiredTmpfs,
    IReadOnlyList<string> RequiredEnvAssignments,
    string? CurrentImageId);

internal static class LiveContainerSpec
{
    internal static LiveContainerExpectation ForSandbox(ContainerContext context, string? currentImageId)
    {
        ArgumentNullException.ThrowIfNull(context);
        string[] env = [.. context.ContainerEnvironment
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(static pair => pair.Key + "=" + pair.Value)];
        return new LiveContainerExpectation(
            "kautuka-sandbox",
            context.HostWorkspacePath,
            context.ContainerWorkspacePath,
            MountReadOnly: false,
            PidsLimit: 256,
            ["/tmp", "/home/node/.kautuka"],
            env,
            currentImageId);
    }

    internal static LiveContainerExpectation ForConnector(
        ContainerContext? mountedWorkspace,
        IReadOnlyList<string> envAssignments,
        string? currentImageId)
        => ForConnector(
            mountedWorkspace?.HostWorkspacePath,
            mountedWorkspace?.ContainerWorkspacePath ?? string.Empty,
            mountedWorkspace is not null,
            envAssignments,
            currentImageId);

    internal static LiveContainerExpectation ForConnector(
        string? hostMountPath,
        string containerMountPath,
        bool mountReadOnly,
        IReadOnlyList<string> envAssignments,
        string? currentImageId)
        => new(
            "kautuka-sandbox",
            hostMountPath,
            containerMountPath,
            mountReadOnly,
            PidsLimit: 128,
            ["/tmp"],
            envAssignments,
            currentImageId);

    internal static string? DescribeDrift(string inspectJson, LiveContainerExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using JsonDocument document = JsonDocument.Parse(inspectJson);
        JsonElement container = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement[0]
            : document.RootElement;
        JsonElement host = container.GetProperty("HostConfig");
        JsonElement config = container.GetProperty("Config");

        if (!string.IsNullOrWhiteSpace(expected.CurrentImageId))
        {
            string imageId = container.TryGetProperty("Image", out JsonElement image) ? image.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(imageId, expected.CurrentImageId, StringComparison.Ordinal))
            {
                return "its image is not the image that tag points at now";
            }
        }

        string? networkDrift = DescribeNetworkDrift(container, host, expected.NetworkName);
        if (networkDrift is not null)
        {
            return networkDrift;
        }

        if (PublishesHostPort(host))
        {
            return "it publishes a host port";
        }

        if (!MountMatches(host, expected) || !ResolvedBindsMatch(container, expected))
        {
            return "its mounts do not match the workspace";
        }

        if (!CapDropIncludesAll(container, host) || CapAddIsPresent(host))
        {
            return "its capabilities do not match the spec";
        }

        if (host.TryGetProperty("Privileged", out JsonElement privileged) && privileged.ValueKind == JsonValueKind.True)
        {
            return "it is privileged";
        }

        if (host.TryGetProperty("Devices", out JsonElement devices)
            && devices.ValueKind == JsonValueKind.Array
            && devices.GetArrayLength() > 0)
        {
            return "it has a host device attached";
        }

        if (IsHostNamespace(host, "PidMode")
            || IsHostNamespace(host, "IpcMode")
            || IsHostNamespace(host, "UsernsMode")
            || IsHostNamespace(host, "CgroupnsMode"))
        {
            return "it shares a host namespace";
        }

        if (!host.TryGetProperty("ReadonlyRootfs", out JsonElement readOnly) || readOnly.ValueKind != JsonValueKind.True)
        {
            return "its root filesystem is writable";
        }

        if (!host.TryGetProperty("PidsLimit", out JsonElement pids) || pids.GetInt64() != expected.PidsLimit)
        {
            return "its process limit does not match the spec";
        }

        if (!SecurityOptMatches(host))
        {
            return "its security options do not match the spec";
        }

        if (!TmpfsMatches(host, expected.RequiredTmpfs))
        {
            return "its temporary mounts do not match the spec";
        }

        if (!EnvMatches(config, expected.RequiredEnvAssignments))
        {
            return "its environment does not match the spec";
        }

        return null;
    }

    internal static string? DescribeSidecarDrift(
        string inspectJson,
        string image,
        IReadOnlyList<WorkspaceSidecarPortBinding> ports,
        string? currentImageId,
        IReadOnlyList<WorkspaceSidecarVolumeBinding>? volumes = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string expectedNetwork = "kautuka-sandbox")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentNullException.ThrowIfNull(ports);
        using JsonDocument document = JsonDocument.Parse(inspectJson);
        JsonElement container = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement[0]
            : document.RootElement;
        if (!string.IsNullOrWhiteSpace(currentImageId))
        {
            string imageId = container.TryGetProperty("Image", out JsonElement idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(imageId, currentImageId, StringComparison.Ordinal))
            {
                return "its image is not the image that tag points at now";
            }
        }

        string observedImage = container.GetProperty("Config").TryGetProperty("Image", out JsonElement imageElement)
            ? imageElement.GetString() ?? string.Empty
            : string.Empty;
        if (!ImageNameMatches(image, observedImage))
        {
            return "it is running " + observedImage;
        }

        if (!PortsMatch(container.GetProperty("HostConfig"), ports))
        {
            return "its published ports do not match the spec";
        }

        JsonElement host = container.GetProperty("HostConfig");
        string? networkDrift = DescribeNetworkDrift(container, host, expectedNetwork);
        if (networkDrift is not null)
        {
            return networkDrift;
        }

        if (!SidecarBindsMatch(host, volumes))
        {
            return "its mounts do not match the spec";
        }

        if (host.TryGetProperty("Privileged", out JsonElement privileged) && privileged.ValueKind == JsonValueKind.True)
        {
            return "it is privileged";
        }

        if (host.TryGetProperty("Devices", out JsonElement devices)
            && devices.ValueKind == JsonValueKind.Array
            && devices.GetArrayLength() > 0)
        {
            return "it has a host device attached";
        }

        if (IsHostNamespace(host, "PidMode")
            || IsHostNamespace(host, "IpcMode")
            || IsHostNamespace(host, "UsernsMode")
            || IsHostNamespace(host, "CgroupnsMode"))
        {
            return "it shares a host namespace";
        }

        string[] requiredEnv = environment is null
            ? []
            : [.. environment
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .Select(static pair => pair.Key + "=" + pair.Value)];
        if (!EnvMatches(container.GetProperty("Config"), requiredEnv))
        {
            return "its environment does not match the spec";
        }

        return null;
    }

    private static string? DescribeNetworkDrift(JsonElement container, JsonElement host, string expectedNetwork)
    {
        string mode = host.TryGetProperty("NetworkMode", out JsonElement networkMode)
            && networkMode.ValueKind == JsonValueKind.String
            ? networkMode.GetString() ?? string.Empty
            : string.Empty;
        // Podman reports "bridge" for named bridge networks. NetworkMode describes
        // the mode; NetworkSettings.Networks identifies the actual attachments.
        if (!string.Equals(mode, "bridge", StringComparison.Ordinal)
            && !string.Equals(mode, expectedNetwork, StringComparison.Ordinal))
        {
            return "its network is " + mode;
        }

        if (!container.TryGetProperty("NetworkSettings", out JsonElement settings)
            || settings.ValueKind != JsonValueKind.Object
            || !settings.TryGetProperty("Networks", out JsonElement networks)
            || networks.ValueKind != JsonValueKind.Object)
        {
            return "its network attachments do not match the spec";
        }

        JsonElement.ObjectEnumerator attachments = networks.EnumerateObject();
        if (!attachments.MoveNext()
            || !string.Equals(attachments.Current.Name, expectedNetwork, StringComparison.Ordinal)
            || attachments.Current.Value.ValueKind != JsonValueKind.Object
            || attachments.MoveNext())
        {
            return "its network attachments do not match the spec";
        }

        return null;
    }

    internal static bool TryReadLabels(string inspectJson, out string? state, out long desiredVersion, out string fingerprint)
    {
        state = null;
        desiredVersion = 0;
        fingerprint = string.Empty;
        using JsonDocument document = JsonDocument.Parse(inspectJson);
        JsonElement container = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement[0]
            : document.RootElement;
        if (container.TryGetProperty("State", out JsonElement stateElement)
            && stateElement.TryGetProperty("Status", out JsonElement status))
        {
            state = status.GetString();
        }

        if (!container.TryGetProperty("Config", out JsonElement config)
            || !config.TryGetProperty("Labels", out JsonElement labels)
            || labels.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (labels.TryGetProperty("kautuka.desired-version", out JsonElement version))
        {
            _ = long.TryParse(version.GetString(), System.Globalization.CultureInfo.InvariantCulture, out desiredVersion);
        }

        if (labels.TryGetProperty("kautuka.spec-fingerprint", out JsonElement fingerprintElement))
        {
            fingerprint = fingerprintElement.GetString() ?? string.Empty;
        }

        return true;
    }

    private static bool MountMatches(JsonElement host, LiveContainerExpectation expected)
    {
        bool hasBinds = host.TryGetProperty("Binds", out JsonElement binds)
            && binds.ValueKind == JsonValueKind.Array
            && binds.GetArrayLength() > 0;
        if (string.IsNullOrWhiteSpace(expected.HostMountPath))
        {
            return !hasBinds;
        }

        if (!hasBinds || binds.GetArrayLength() != 1)
        {
            return false;
        }

        if (!TryReadBind(binds[0].GetString() ?? string.Empty, out string source, out string destination, out bool readOnly))
        {
            return false;
        }

        return string.Equals(destination, expected.ContainerMountPath, StringComparison.Ordinal)
            && readOnly == expected.MountReadOnly
            && MountSourceMatches(expected.HostMountPath, source);
    }

    private static bool ResolvedBindsMatch(JsonElement container, LiveContainerExpectation expected)
    {
        if (!container.TryGetProperty("Mounts", out JsonElement mounts) || mounts.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (JsonElement mount in mounts.EnumerateArray())
        {
            string type = mount.TryGetProperty("Type", out JsonElement typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(type, "bind", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(expected.HostMountPath))
            {
                return false;
            }

            string destination = mount.TryGetProperty("Destination", out JsonElement destinationElement)
                ? destinationElement.GetString() ?? string.Empty
                : string.Empty;
            string source = mount.TryGetProperty("Source", out JsonElement sourceElement)
                ? sourceElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(destination, expected.ContainerMountPath, StringComparison.Ordinal)
                || !MountSourceMatches(expected.HostMountPath, source))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadBind(string value, out string source, out string destination, out bool readOnly)
    {
        source = string.Empty;
        destination = string.Empty;
        readOnly = false;
        string rest = value;
        if (rest.EndsWith(":ro", StringComparison.Ordinal))
        {
            readOnly = true;
            rest = rest[..^3];
        }
        else if (rest.EndsWith(":rw", StringComparison.Ordinal))
        {
            rest = rest[..^3];
        }
        else
        {
            int optionSplit = rest.LastIndexOf(':');
            if (optionSplit > 0)
            {
                string tail = rest[(optionSplit + 1)..];
                if (tail.Length > 0
                    && tail.IndexOf('/', StringComparison.Ordinal) < 0
                    && tail.IndexOf('\\', StringComparison.Ordinal) < 0)
                {
                    readOnly = tail.Split(',').Any(static part => string.Equals(part, "ro", StringComparison.Ordinal));
                    rest = rest[..optionSplit];
                }
            }
        }

        int split = rest.LastIndexOf(':');
        if (split <= 0 || split >= rest.Length - 1)
        {
            return false;
        }

        source = rest[..split];
        destination = rest[(split + 1)..];
        return true;
    }

    private static bool MountSourceMatches(string expected, string observed)
    {
        string left = NormalizeMountPath(expected);
        string right = NormalizeMountPath(observed);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string path = left;
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            path = path[2..];
        }

        if (string.Equals(path, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!right.EndsWith(path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Podman Machine and WSL insert a drive root in front of the Windows path.
        // Any other parent is a different directory.
        string prefix = right[..^path.Length].TrimEnd('/');
        return IsRuntimeDriveRoot(prefix);
    }

    private static bool IsRuntimeDriveRoot(string prefix)
    {
        const string desktopRoot = "/run/desktop/mnt/host/";
        const string wslRoot = "/mnt/";
        string remainder;
        if (prefix.StartsWith(desktopRoot, StringComparison.OrdinalIgnoreCase))
        {
            remainder = prefix[desktopRoot.Length..];
        }
        else if (prefix.StartsWith(wslRoot, StringComparison.OrdinalIgnoreCase))
        {
            remainder = prefix[wslRoot.Length..];
        }
        else
        {
            return false;
        }

        return remainder.Length == 1 && char.IsLetter(remainder[0]);
    }

    private static string NormalizeMountPath(string path)
        => path.Replace('\\', '/').TrimEnd('/');

    private static bool PublishesHostPort(JsonElement host)
    {
        if (host.TryGetProperty("PublishAllPorts", out JsonElement publishAll) && publishAll.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return host.TryGetProperty("PortBindings", out JsonElement ports)
            && ports.ValueKind == JsonValueKind.Object
            && ports.EnumerateObject().Any();
    }

    private static bool IsHostNamespace(JsonElement host, string name)
        => host.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), "host", StringComparison.OrdinalIgnoreCase);

    private static bool CapDropIncludesAll(JsonElement container, JsonElement host)
    {
        // Podman expands --cap-drop ALL into its default capability names. Its
        // resolved sets are authoritative; an empty Go slice can serialize as null.
        bool hasEffective = container.TryGetProperty("EffectiveCaps", out JsonElement effective);
        bool hasBounding = container.TryGetProperty("BoundingCaps", out JsonElement bounding);
        if (hasEffective || hasBounding)
        {
            return hasEffective && hasBounding && IsEmptyCapabilitySet(effective) && IsEmptyCapabilitySet(bounding);
        }

        if (!host.TryGetProperty("CapDrop", out JsonElement capDrop) || capDrop.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in capDrop.EnumerateArray())
        {
            if (string.Equals(item.GetString(), "ALL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEmptyCapabilitySet(JsonElement value)
        => value.ValueKind == JsonValueKind.Null
            || (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);

    private static bool CapAddIsPresent(JsonElement host)
        => host.TryGetProperty("CapAdd", out JsonElement capAdd)
            && capAdd.ValueKind == JsonValueKind.Array
            && capAdd.GetArrayLength() > 0;

    private static bool SecurityOptMatches(JsonElement host)
    {
        if (!host.TryGetProperty("SecurityOpt", out JsonElement options) || options.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        bool found = false;
        foreach (JsonElement option in options.EnumerateArray())
        {
            string value = option.GetString() ?? string.Empty;
            if (string.Equals(value, "no-new-privileges", StringComparison.Ordinal))
            {
                found = true;
                continue;
            }

            if (value.StartsWith("label=", StringComparison.Ordinal))
            {
                continue;
            }

            return false;
        }

        return found;
    }

    private static bool TmpfsMatches(JsonElement host, IReadOnlyList<string> required)
    {
        if (!host.TryGetProperty("Tmpfs", out JsonElement tmpfs) || tmpfs.ValueKind != JsonValueKind.Object)
        {
            return required.Count == 0;
        }

        foreach (string path in required)
        {
            if (!tmpfs.TryGetProperty(path, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EnvMatches(JsonElement config, IReadOnlyList<string> required)
    {
        if (!config.TryGetProperty("Env", out JsonElement env) || env.ValueKind != JsonValueKind.Array)
        {
            return required.Count == 0;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in env.EnumerateArray())
        {
            string? value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                present.Add(value);
            }
        }

        foreach (string assignment in required)
        {
            if (!present.Contains(assignment))
            {
                return false;
            }
        }

        foreach (string assignment in present)
        {
            int split = assignment.IndexOf('=', StringComparison.Ordinal);
            string key = split < 0 ? assignment : assignment[..split];
            if (injectedProcessKeys.Contains(key) && !required.Contains(assignment))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<string> injectedProcessKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT",
        "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",
        "NODE_OPTIONS", "NODE_PATH",
        "PYTHONPATH", "PYTHONSTARTUP", "PYTHONHOME",
        "BASH_ENV", "SHELLOPTS",
        "SSLKEYLOGFILE",
    };

    private static bool ImageNameMatches(string expected, string observed)
    {
        if (string.Equals(expected, observed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string localhostPrefix = "localhost/";
        string expectedName = expected.StartsWith(localhostPrefix, StringComparison.OrdinalIgnoreCase)
            ? expected[localhostPrefix.Length..]
            : expected;
        string observedName = observed.StartsWith(localhostPrefix, StringComparison.OrdinalIgnoreCase)
            ? observed[localhostPrefix.Length..]
            : observed;
        return string.Equals(expectedName, observedName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SidecarBindsMatch(JsonElement host, IReadOnlyList<WorkspaceSidecarVolumeBinding>? volumes)
    {
        bool hasBinds = host.TryGetProperty("Binds", out JsonElement binds)
            && binds.ValueKind == JsonValueKind.Array
            && binds.GetArrayLength() > 0;
        int expectedCount = volumes?.Count ?? 0;
        if (!hasBinds)
        {
            return expectedCount == 0;
        }

        if (binds.GetArrayLength() != expectedCount || volumes is null)
        {
            return false;
        }

        var unmatched = new List<int>(expectedCount);
        for (int index = 0; index < binds.GetArrayLength(); index++)
        {
            unmatched.Add(index);
        }

        foreach (WorkspaceSidecarVolumeBinding volume in volumes)
        {
            int found = -1;
            foreach (int index in unmatched)
            {
                if (!TryReadBind(binds[index].GetString() ?? string.Empty, out string source, out string destination, out bool readOnly))
                {
                    continue;
                }

                if (string.Equals(destination, volume.ContainerPath, StringComparison.Ordinal)
                    && readOnly == volume.ReadOnly
                    && MountSourceMatches(volume.HostPath, source))
                {
                    found = index;
                    break;
                }
            }

            if (found < 0)
            {
                return false;
            }

            _ = unmatched.Remove(found);
        }

        return unmatched.Count == 0;
    }

    private static bool PortsMatch(JsonElement host, IReadOnlyList<WorkspaceSidecarPortBinding> ports)
    {
        if (!host.TryGetProperty("PortBindings", out JsonElement bindings) || bindings.ValueKind != JsonValueKind.Object)
        {
            return ports.Count == 0;
        }

        int observedCount = 0;
        foreach (JsonProperty _ in bindings.EnumerateObject())
        {
            observedCount++;
        }

        if (observedCount != ports.Count)
        {
            return false;
        }

        foreach (WorkspaceSidecarPortBinding port in ports)
        {
            string key = port.ContainerPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/tcp";
            if (!bindings.TryGetProperty(key, out JsonElement published)
                || published.ValueKind != JsonValueKind.Array
                || published.GetArrayLength() != 1)
            {
                return false;
            }

            bool found = false;
            foreach (JsonElement entry in published.EnumerateArray())
            {
                string hostPort = entry.TryGetProperty("HostPort", out JsonElement hostPortElement) ? hostPortElement.GetString() ?? string.Empty : string.Empty;
                string hostIp = entry.TryGetProperty("HostIp", out JsonElement hostIpElement) ? hostIpElement.GetString() ?? string.Empty : string.Empty;
                if (!string.Equals(hostPort, port.HostPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(port.HostBindAddress)
                    && !string.Equals(hostIp, port.HostBindAddress, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found = true;
                break;
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }
}
