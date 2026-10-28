using System.Globalization;

namespace Kautuka.Control.Core.Services;

/// <summary>Identifies a Kautuka backend service.</summary>
public enum ServiceId
{
    Gateway,
    ChannelHost,
    WorkspaceHost,
    /// <summary>The Kautuka desktop host (WebView + tray).</summary>
    UiHost,
}

public static class ServiceIdExtensions
{
    public static ServiceId[] BackendServices() =>
        [ServiceId.ChannelHost, ServiceId.WorkspaceHost, ServiceId.Gateway];

    public static ServiceId[] AllServices() =>
        [ServiceId.Gateway, ServiceId.ChannelHost, ServiceId.WorkspaceHost, ServiceId.UiHost];

    public static string FolderName(this ServiceId id) => id switch
    {
        ServiceId.Gateway => "gateway",
        ServiceId.ChannelHost => "channelhost",
        ServiceId.WorkspaceHost => "workspacehost",
        ServiceId.UiHost => "uihost",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static string DisplayName(this ServiceId id) => id switch
    {
        ServiceId.Gateway => "Kautuka Gateway",
        ServiceId.ChannelHost => "Kautuka ChannelHost",
        ServiceId.WorkspaceHost => "Kautuka WorkspaceHost",
        ServiceId.UiHost => "Kautuka UI Host",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static string ServiceName(this ServiceId id) => id switch
    {
        ServiceId.Gateway => "KautukaGateway",
        ServiceId.ChannelHost => "KautukaChannelHost",
        ServiceId.WorkspaceHost => "KautukaWorkspaceHost",
        ServiceId.UiHost => "KautukaUiHost",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static string BinaryName(this ServiceId id)
    {
        var ext = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return id switch
        {
            ServiceId.Gateway => $"Kautuka.Gateway{ext}",
            ServiceId.ChannelHost => $"Kautuka.ChannelHost{ext}",
            ServiceId.WorkspaceHost => $"Kautuka.WorkspaceHost{ext}",
            ServiceId.UiHost => $"Kautuka.Control.Desktop{ext}",
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    public static string InstallRelativeDirectory(this ServiceId id) => id switch
    {
        ServiceId.Gateway => "core",
        ServiceId.ChannelHost => "core",
        ServiceId.WorkspaceHost => "core",
        ServiceId.UiHost => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static string ExePath(this ServiceId id, string installDir)
    {
        var relativeDir = id.InstallRelativeDirectory();
        return string.IsNullOrWhiteSpace(relativeDir)
            ? Path.Combine(installDir, id.BinaryName())
            : Path.Combine(installDir, relativeDir, id.BinaryName());
    }

    public static string ProjectPath(this ServiceId id) => id switch
    {
        ServiceId.Gateway => Path.Combine("kautuka-core", "Kautuka.Gateway", "Kautuka.Gateway.csproj"),
        ServiceId.ChannelHost => Path.Combine("kautuka-core", "Kautuka.ChannelHost", "Kautuka.ChannelHost.csproj"),
        ServiceId.WorkspaceHost => Path.Combine("kautuka-core", "Kautuka.WorkspaceHost", "Kautuka.WorkspaceHost.csproj"),
        ServiceId.UiHost => Path.Combine("kautuka-app", "Kautuka.Control.Desktop", "Kautuka.Control.Desktop.csproj"),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static ServiceId[] DependsOn(this ServiceId id) => id switch
    {
        ServiceId.ChannelHost => [ServiceId.Gateway],
        ServiceId.WorkspaceHost => [ServiceId.Gateway],
        ServiceId.UiHost => [ServiceId.Gateway],
        _ => [],
    };

    public static string[] StartArguments(this ServiceId id,
        Config.PortAssignments ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        return id switch
        {
            ServiceId.Gateway =>
            [
                "--KAUTUKA_PUBLIC_PORT", ports.GatewayPublic.ToString(CultureInfo.InvariantCulture),
                "--KAUTUKA_INTERNAL_PORT", "8099",
                "--CHANNELHOST_GRPC_URL", $"http://127.0.0.1:{ports.ChannelHost}",
                "--WORKSPACE_GRPC_URL", $"http://127.0.0.1:{ports.WorkspaceGrpc}",
            ],
            ServiceId.ChannelHost =>
            [
                "--CHANNELHOST_GRPC_PORT", ports.ChannelHost.ToString(CultureInfo.InvariantCulture),
            ],
            ServiceId.WorkspaceHost =>
            [
                "--WorkspaceGrpc:Port", ports.WorkspaceGrpc.ToString(CultureInfo.InvariantCulture),
                "--Ports:WorkspaceGrpc", ports.WorkspaceGrpc.ToString(CultureInfo.InvariantCulture),
            ],
            ServiceId.UiHost =>
            [
                "--gateway", $"http://127.0.0.1:{ports.GatewayPublic}",
                "--ui-port", ports.UiHost.ToString(CultureInfo.InvariantCulture),
            ],
            _ => [],
        };
    }

    // Compatibility for non-sidecar managers that still materialize env blocks.
    // Sidecar startup path now uses StartArguments() and does not depend on env vars.
    public static Dictionary<string, string> EnvVars(this ServiceId id,
        Config.PortAssignments ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        return id switch
        {
            ServiceId.Gateway => new()
            {
                ["KAUTUKA_PUBLIC_PORT"] = ports.GatewayPublic.ToString(CultureInfo.InvariantCulture),
                ["KAUTUKA_INTERNAL_PORT"] = "8099",
                ["CHANNELHOST_GRPC_URL"] = $"http://127.0.0.1:{ports.ChannelHost}",
                ["WORKSPACE_GRPC_URL"] = $"http://127.0.0.1:{ports.WorkspaceGrpc}",
            },
            ServiceId.ChannelHost => new()
            {
                ["CHANNELHOST_GRPC_PORT"] = ports.ChannelHost.ToString(CultureInfo.InvariantCulture),
            },
            ServiceId.WorkspaceHost => new()
            {
                ["WorkspaceGrpc__Port"] = ports.WorkspaceGrpc.ToString(CultureInfo.InvariantCulture),
                ["Ports__WorkspaceGrpc"] = ports.WorkspaceGrpc.ToString(CultureInfo.InvariantCulture),
            },
            ServiceId.UiHost => new()
            {
                ["KAUTUKA_GATEWAY_URL"] = $"http://127.0.0.1:{ports.GatewayPublic}",
                ["KAUTUKA_UI_PORT"] = ports.UiHost.ToString(CultureInfo.InvariantCulture),
            },
            _ => [],
        };
    }
}
