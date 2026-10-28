using System.Text.Json.Serialization;

using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.State;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(UpdateStateRecord))]
[JsonSerializable(typeof(UpdateOperationRecord))]
[JsonSerializable(typeof(List<UpdateOperationRecord>))]
internal sealed partial class UpdateStateJsonContext : JsonSerializerContext
{
}
