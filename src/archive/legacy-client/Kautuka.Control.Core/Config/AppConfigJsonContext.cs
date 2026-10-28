using System.Text.Json.Serialization;

namespace Kautuka.Control.Core.Config;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(DesktopConfig))]
[JsonSerializable(typeof(PortAssignments))]
public sealed partial class AppConfigJsonContext : JsonSerializerContext;
