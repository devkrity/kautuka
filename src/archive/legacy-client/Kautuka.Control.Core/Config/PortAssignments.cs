using System.Text.Json.Serialization;

namespace Kautuka.Control.Core.Config;

public sealed record PortAssignments
{
    [JsonPropertyName("gateway_public")]
    public ushort GatewayPublic { get; init; } = 8000;

    [JsonPropertyName("ui_dev")]
    public ushort UiHost { get; init; } = 8001;

    [JsonPropertyName("channelhost")]
    public ushort ChannelHost { get; init; } = 8098;

    [JsonPropertyName("workspace_grpc")]
    public ushort WorkspaceGrpc { get; init; } = 8100;

    public static PortAssignments Defaults() => new();
}
