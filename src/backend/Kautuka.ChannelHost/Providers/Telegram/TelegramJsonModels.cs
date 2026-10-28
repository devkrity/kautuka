using System.Text.Json.Serialization;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed record TelegramGetUpdatesRequest(
    int Timeout,
    long Offset,
    [property: JsonPropertyName("allowed_updates")] string[] AllowedUpdates);
