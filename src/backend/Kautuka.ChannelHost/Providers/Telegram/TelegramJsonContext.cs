using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kautuka.ChannelHost.Providers.Telegram;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(TelegramGetUpdatesRequest))]
internal sealed partial class TelegramJsonContext : JsonSerializerContext
{
}
