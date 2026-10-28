using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kautuka.Gateway.Endpoints.Common;

internal static class GatewayEndpointUtilities
{
    internal static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");

    internal static GatewayUserPayload ToUserPayload(GatewayUser user, string[]? externalProviders = null) => new(
        user.Id,
        user.Username,
        user.Email,
        user.DisplayName,
        user.Role,
        user.IsActive,
        user.CanImportConnectors,
        user.CanImportSkills,
        user.HasPassword,
        externalProviders ?? []);

    internal static async Task WriteSse<T>(HttpContext context, string evt, T data, JsonTypeInfo<T> jsonTypeInfo)
    {
        string payload = JsonSerializer.Serialize(data, jsonTypeInfo);
        await context.Response.WriteAsync($"event: {evt}\n", Encoding.UTF8).ConfigureAwait(false);
        await context.Response.WriteAsync($"data: {payload}\n\n", Encoding.UTF8).ConfigureAwait(false);
        await context.Response.Body.FlushAsync().ConfigureAwait(false);
    }

    internal static IEnumerable<string> Chunk(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        for (int idx = 0; idx < text.Length; idx += chunkSize)
        {
            int take = Math.Min(chunkSize, text.Length - idx);
            yield return text.Substring(idx, take);
        }
    }
}
