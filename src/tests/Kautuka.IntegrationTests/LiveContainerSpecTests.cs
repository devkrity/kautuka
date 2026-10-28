using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.IntegrationTests;

public sealed class LiveContainerSpecTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExpandedPodmanCapDropUsesResolvedCapabilitySets(bool nullSets)
    {
        JsonNode container = JsonNode.Parse(Inspect(mount: "/tmp/kautuka/alpha:/workspace:rprivate,rbind", capDrop: "CAP_CHOWN"))![0]!;
        container["HostConfig"]!["CapDrop"] = new JsonArray("CAP_CHOWN", "CAP_DAC_OVERRIDE", "CAP_FOWNER", "CAP_FSETID",
            "CAP_KILL", "CAP_NET_BIND_SERVICE", "CAP_SETFCAP", "CAP_SETGID", "CAP_SETPCAP", "CAP_SETUID", "CAP_SYS_CHROOT");
        container["EffectiveCaps"] = nullSets ? null : new JsonArray();
        container["BoundingCaps"] = nullSets ? null : new JsonArray();
        LiveContainerExpectation expected = LiveContainerSpec.ForSandbox(Sandbox(), "sha256:current");
        Assert.Null(LiveContainerSpec.DescribeDrift(container.ToJsonString(), expected));

        container["EffectiveCaps"] = new JsonArray("CAP_CHOWN");
        Assert.Equal("its capabilities do not match the spec", LiveContainerSpec.DescribeDrift(container.ToJsonString(), expected));
        container["EffectiveCaps"] = new JsonArray();
        container["BoundingCaps"] = new JsonArray("CAP_CHOWN");
        Assert.Equal("its capabilities do not match the spec", LiveContainerSpec.DescribeDrift(container.ToJsonString(), expected));
        container["BoundingCaps"] = new JsonArray();
        container["HostConfig"]!["CapAdd"] = new JsonArray("CAP_SYS_ADMIN");
        Assert.Equal("its capabilities do not match the spec", LiveContainerSpec.DescribeDrift(container.ToJsonString(), expected));
    }

    [Fact]
    public void ConnectorInspectionUsesActualReadOnlyWorkspaceMount()
    {
        LiveContainerExpectation expected = LiveContainerSpec.ForConnector(Sandbox(), ["TOKEN=secret"], "sha256:current");
        Assert.Null(LiveContainerSpec.DescribeDrift(Inspect(mount: "/tmp/kautuka/alpha:/workspace:ro", pidsLimit: 128), expected));
        Assert.Equal("its mounts do not match the workspace",
            LiveContainerSpec.DescribeDrift(Inspect(mount: "/tmp/kautuka/alpha:/workspace:rw", pidsLimit: 128), expected));
        Assert.Equal("its mounts do not match the workspace",
            LiveContainerSpec.DescribeDrift(Inspect(mount: "/other:/workspace:ro", pidsLimit: 128), expected));
        Assert.Equal("its mounts do not match the workspace",
            LiveContainerSpec.DescribeDrift(Inspect(mount: "/tmp/kautuka/alpha:/other:ro", pidsLimit: 128), expected));
    }

    [Fact]
    public void IsolatedConnectorStillRejectsWorkspaceMounts()
    {
        LiveContainerExpectation expected = LiveContainerSpec.ForConnector(null, ["TOKEN=secret"], "sha256:current");
        Assert.Equal("its mounts do not match the workspace",
            LiveContainerSpec.DescribeDrift(Inspect(pidsLimit: 128), expected));
    }

    [Fact]
    public void MatchingInspectHasNoDrift()
    {
        ContainerContext context = Sandbox();
        string? drift = LiveContainerSpec.DescribeDrift(Inspect(), LiveContainerSpec.ForSandbox(context, "sha256:current"));
        Assert.Null(drift);
    }

    [Fact]
    public void ChangedMountNetworkEnvironmentAndImageAreDrift()
    {
        ContainerContext context = Sandbox();
        LiveContainerExpectation expected = LiveContainerSpec.ForSandbox(context, "sha256:current");
        Assert.Equal("its image is not the image that tag points at now", LiveContainerSpec.DescribeDrift(Inspect(imageId: "sha256:old"), expected));
        Assert.Equal("its network attachments do not match the spec", LiveContainerSpec.DescribeDrift(Inspect(network: "podman"), expected));
        Assert.Equal("it publishes a host port", LiveContainerSpec.DescribeDrift(Inspect(publishPort: true), expected));
        Assert.Equal("its mounts do not match the workspace", LiveContainerSpec.DescribeDrift(Inspect(mount: "/other:/workspace"), expected));
        Assert.Equal("its mounts do not match the workspace", LiveContainerSpec.DescribeDrift(Inspect(mount: "/other/alpha:/workspace"), expected));
        Assert.Null(LiveContainerSpec.DescribeDrift(Inspect(resolvedExtra: string.Empty), expected));
        Assert.Equal("its mounts do not match the workspace", LiveContainerSpec.DescribeDrift(Inspect(resolvedExtra: "/etc"), expected));
        Assert.Null(LiveContainerSpec.DescribeDrift(Inspect(mount: "/run/desktop/mnt/host/d/tmp/kautuka/alpha:/workspace"), LiveContainerSpec.ForSandbox(WindowsSandbox(), "sha256:current")));
        Assert.Null(LiveContainerSpec.DescribeDrift(Inspect(mount: "/mnt/d/tmp/kautuka/alpha:/workspace"), LiveContainerSpec.ForSandbox(WindowsSandbox(), "sha256:current")));
        Assert.Equal("its mounts do not match the workspace", LiveContainerSpec.DescribeDrift(Inspect(mount: "/stolen/tmp/kautuka/alpha:/workspace"), LiveContainerSpec.ForSandbox(WindowsSandbox(), "sha256:current")));
        Assert.Equal("its environment does not match the spec", LiveContainerSpec.DescribeDrift(Inspect(env: "PATH=/usr/bin"), expected));
        Assert.Equal("its environment does not match the spec", LiveContainerSpec.DescribeDrift(Inspect(injected: "LD_PRELOAD=/tmp/x"), expected));
        Assert.Equal("its root filesystem is writable", LiveContainerSpec.DescribeDrift(Inspect(readOnly: false), expected));
        Assert.Equal("its capabilities do not match the spec", LiveContainerSpec.DescribeDrift(Inspect(capDrop: "NET_RAW"), expected));
        Assert.Equal("it is privileged", LiveContainerSpec.DescribeDrift(Inspect(privileged: true), expected));
        Assert.Equal("it has a host device attached", LiveContainerSpec.DescribeDrift(Inspect(device: "/dev/fuse"), expected));
        Assert.Equal("it shares a host namespace", LiveContainerSpec.DescribeDrift(Inspect(pidMode: "host"), expected));
        Assert.Null(LiveContainerSpec.DescribeDrift(Inspect(pidMode: "private"), expected));
    }

    [Fact]
    public void SidecarImageAndPortsAreCompared()
    {
        WorkspaceSidecarPortBinding[] ports = [new(8080, 80, "127.0.0.1")];
        Assert.Null(LiveContainerSpec.DescribeSidecarDrift(SidecarInspect(), "example/sidecar:1", ports, "sha256:sidecar"));
        Assert.Equal(
            "its image is not the image that tag points at now",
            LiveContainerSpec.DescribeSidecarDrift(SidecarInspect(imageId: "sha256:other"), "example/sidecar:1", ports, "sha256:sidecar"));
        Assert.Equal(
            "its published ports do not match the spec",
            LiveContainerSpec.DescribeSidecarDrift(SidecarInspect(hostPort: "9090"), "example/sidecar:1", ports, null));
        Assert.Equal(
            "its published ports do not match the spec",
            LiveContainerSpec.DescribeSidecarDrift(SidecarInspect(extraBinding: true), "example/sidecar:1", ports, null));
        WorkspaceSidecarVolumeBinding[] volumes = [new("/var/lib/pkgs", "/data", true)];
        Assert.Null(LiveContainerSpec.DescribeSidecarDrift(
            SidecarInspect(binds: ["/var/lib/pkgs:/data:ro"]),
            "example/sidecar:1",
            ports,
            "sha256:sidecar",
            volumes));
        Assert.Equal(
            "its mounts do not match the spec",
            LiveContainerSpec.DescribeSidecarDrift(
                SidecarInspect(binds: ["/var/lib/pkgs:/data:ro", "/etc:/host-etc:ro"]),
                "example/sidecar:1",
                ports,
                null,
                volumes));
        Assert.Equal(
            "its environment does not match the spec",
            LiveContainerSpec.DescribeSidecarDrift(
                SidecarInspect(injected: "LD_PRELOAD=/tmp/x"),
                "example/sidecar:1",
                ports,
                null));
        Assert.Equal(
            "it is privileged",
            LiveContainerSpec.DescribeSidecarDrift(SidecarInspect(privileged: true), "example/sidecar:1", ports, null));
        Assert.Equal(
            "it shares a host namespace",
            LiveContainerSpec.DescribeSidecarDrift(SidecarInspect(pidMode: "host"), "example/sidecar:1", ports, null));
        Assert.Equal(
            "its network attachments do not match the spec",
            LiveContainerSpec.DescribeSidecarDrift(
                SidecarInspect(),
                "example/sidecar:1",
                ports,
                null,
                expectedNetwork: "kautuka-telemetry"));
    }

    [Theory]
    [InlineData("bridge")]
    [InlineData("kautuka-sandbox")]
    public void NamedNetworkAttachmentIsAcceptedForSandboxAndConnector(string mode)
    {
        string inspect = Inspect(networkMode: mode);
        Assert.Null(LiveContainerSpec.DescribeDrift(inspect, LiveContainerSpec.ForSandbox(Sandbox(), "sha256:current")));
        LiveContainerExpectation connector = LiveContainerSpec.ForConnector(
            "/tmp/kautuka/alpha", "/workspace", false, ["TOKEN=secret"], "sha256:current");
        Assert.Null(LiveContainerSpec.DescribeDrift(Inspect(networkMode: mode, pidsLimit: 128), connector));
    }

    [Theory]
    [InlineData("kautuka-sandbox")]
    [InlineData("kautuka-telemetry")]
    public void BridgeSidecarUsesActualNamedNetworkAttachment(string network)
    {
        Assert.Null(LiveContainerSpec.DescribeSidecarDrift(
            SidecarInspect(network: network), "example/sidecar:1", [new(8080, 80, "127.0.0.1")],
            "sha256:sidecar", expectedNetwork: network));
    }

    [Theory]
    [InlineData("host", "kautuka-sandbox", null, "its network is host")]
    [InlineData("none", "kautuka-sandbox", null, "its network is none")]
    [InlineData("bridge", "podman", null, "its network attachments do not match the spec")]
    [InlineData("bridge", "kautuka-sandbox", "podman", "its network attachments do not match the spec")]
    [InlineData("kautuka-sandbox", "podman", null, "its network attachments do not match the spec")]
    [InlineData("bridge", null, null, "its network attachments do not match the spec")]
    public void IncorrectOrMissingNetworkAttachmentsRemainDrift(string mode, string? network, string? extraNetwork, string drift)
    {
        Assert.Equal(drift, LiveContainerSpec.DescribeDrift(
            Inspect(network: network, networkMode: mode, extraNetwork: extraNetwork),
            LiveContainerSpec.ForSandbox(Sandbox(), "sha256:current")));
        Assert.Equal(drift, LiveContainerSpec.DescribeSidecarDrift(
            SidecarInspect(network: network, networkMode: mode, extraNetwork: extraNetwork),
            "example/sidecar:1", [new(8080, 80, "127.0.0.1")], "sha256:sidecar"));
    }

    private static object? NetworkSettings(string? network, string? extraNetwork)
        => network is null ? null : new
        {
            Networks = (extraNetwork is null ? new[] { network } : new[] { network, extraNetwork })
                .ToDictionary(static name => name, static _ => new object(), StringComparer.Ordinal),
        };

    private static ContainerContext Sandbox()
        => new(
            "agent",
            "alpha",
            "user",
            "/tmp/kautuka/alpha",
            "/workspace",
            "example/sandbox:1",
            1,
            "512m",
            30,
            ["run_shell"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["TOKEN"] = "secret" });

    private static ContainerContext WindowsSandbox()
        => new(
            "agent",
            "alpha",
            "user",
            @"D:\tmp\kautuka\alpha",
            "/workspace",
            "example/sandbox:1",
            1,
            "512m",
            30,
            ["run_shell"],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["TOKEN"] = "secret" });

    private static string Inspect(
        string imageId = "sha256:current",
        string? network = "kautuka-sandbox",
        string mount = "/tmp/kautuka/alpha:/workspace",
        string env = "TOKEN=secret",
        string? injected = null,
        bool readOnly = true,
        string capDrop = "ALL",
        bool privileged = false,
        string? device = null,
        string? pidMode = null,
        string? resolvedExtra = null,
        bool publishPort = false,
        string networkMode = "bridge",
        string? extraNetwork = null,
        long pidsLimit = 256)
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                Image = imageId,
                NetworkSettings = NetworkSettings(network, extraNetwork),
                State = new { Status = "running" },
                Config = new
                {
                    Env = injected is null ? new[] { "PATH=/usr/bin", env } : new[] { "PATH=/usr/bin", env, injected },
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["kautuka.desired-version"] = "3",
                        ["kautuka.spec-fingerprint"] = "abc",
                    },
                },
                Mounts = resolvedExtra is null
                    ? null
                    : resolvedExtra.Length == 0
                        ? new object[] { new { Type = "bind", Source = "/tmp/kautuka/alpha", Destination = "/workspace" } }
                        : new object[]
                        {
                            new { Type = "bind", Source = "/tmp/kautuka/alpha", Destination = "/workspace" },
                            new { Type = "bind", Source = resolvedExtra, Destination = "/host-etc" },
                        },
                HostConfig = new
                {
                    NetworkMode = networkMode,
                    Binds = new[] { mount },
                    CapDrop = new[] { capDrop },
                    CapAdd = Array.Empty<string>(),
                    Privileged = privileged,
                    Devices = device is null ? Array.Empty<object>() : new object[] { new { PathOnHost = device } },
                    PidMode = pidMode,
                    PublishAllPorts = false,
                    PortBindings = publishPort
                        ? new Dictionary<string, object[]>(StringComparer.Ordinal)
                        {
                            ["22/tcp"] = [new { HostIp = "0.0.0.0", HostPort = "22" }],
                        }
                        : new Dictionary<string, object[]>(StringComparer.Ordinal),
                    ReadonlyRootfs = readOnly,
                    PidsLimit = pidsLimit,
                    SecurityOpt = new[] { "no-new-privileges", "label=disable" },
                    Tmpfs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["/tmp"] = "rw,noexec,nosuid",
                        ["/home/node/.kautuka"] = "rw,nodev,nosuid,mode=1777",
                    },
                },
            },
        });

    private static string SidecarInspect(
        string imageId = "sha256:sidecar",
        string hostPort = "8080",
        bool extraBinding = false,
        string[]? binds = null,
        string? injected = null,
        bool privileged = false,
        string? pidMode = null,
        string? network = "kautuka-sandbox",
        string networkMode = "bridge",
        string? extraNetwork = null)
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                Image = imageId,
                NetworkSettings = NetworkSettings(network, extraNetwork),
                Config = new
                {
                    Image = "example/sidecar:1",
                    Env = injected is null ? new[] { "PATH=/usr/bin" } : new[] { "PATH=/usr/bin", injected },
                },
                HostConfig = new
                {
                    NetworkMode = networkMode,
                    Binds = binds,
                    Privileged = privileged,
                    PidMode = pidMode,
                    PortBindings = new Dictionary<string, object[]>(StringComparer.Ordinal)
                    {
                        ["80/tcp"] = extraBinding
                            ? [new { HostIp = "127.0.0.1", HostPort = hostPort }, new { HostIp = "0.0.0.0", HostPort = hostPort }]
                            : [new { HostIp = "127.0.0.1", HostPort = hostPort }],
                    },
                },
            },
        });
}
