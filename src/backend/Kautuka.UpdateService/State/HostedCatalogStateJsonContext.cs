using System.Text.Json.Serialization;

using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.State;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(HostedCatalogSnapshot))]
[JsonSerializable(typeof(HostedCatalogEntry))]
[JsonSerializable(typeof(List<HostedCatalogEntry>))]
internal sealed partial class HostedCatalogStateJsonContext : JsonSerializerContext
{
}
