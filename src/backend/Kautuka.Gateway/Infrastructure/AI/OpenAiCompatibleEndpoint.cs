using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Runtime;

using OpenAI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal static class OpenAiCompatibleEndpoint
{
    internal const string HttpTimeoutSettingKey = "http_timeout_seconds";

    public static string? ResolveBaseUrl(JsonObject settings, string? defaultBaseUrl)
    {
        string? customUrl = AiProviderSettings.GetString(settings, "base_url");
        return customUrl ?? defaultBaseUrl;
    }

    public static OpenAIClientOptions CreateClientOptions(string baseUrl, HttpMessageHandler? transportHandler)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl.TrimEnd('/')),
        };
        if (transportHandler is not null)
        {
#pragma warning disable CA2000 // OpenAIClientOptions owns the transport HttpClient for the chat client lifetime.
            options.Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(
                new HttpClient(transportHandler, disposeHandler: false));
#pragma warning restore CA2000
        }

        return options;
    }

    public static async Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
        JsonObject settings,
        string? apiKey,
        string? defaultBaseUrl,
        IRunLimitResolver? runLimitResolver,
        bool throwOnFailure,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (throwOnFailure)
                throw new InvalidOperationException("Provider API key is not configured.");

            return [];
        }

        string? baseUrl = ResolveBaseUrl(settings, defaultBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            if (throwOnFailure)
                throw new InvalidOperationException("Provider base URL is not configured.");

            return [];
        }

        try
        {
            using var http = new HttpClient
            {
                Timeout = AiProviderSettings.GetTimeout(
                    settings,
                    HttpTimeoutSettingKey,
                    runLimitResolver?.GetModelTimeout() ?? AiProviderSettings.DefaultHttpTimeout),
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using HttpResponseMessage response = await http.GetAsync(
                new Uri($"{baseUrl.TrimEnd('/')}/models", UriKind.Absolute), cancellationToken).ConfigureAwait(false);
            if (throwOnFailure)
                response.EnsureSuccessStatusCode();
            else if (!response.IsSuccessStatusCode)
                return [];

            using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!payload.RootElement.TryGetProperty("data", out JsonElement dataArray)
                || dataArray.ValueKind != JsonValueKind.Array)
            {
                if (throwOnFailure)
                    throw new JsonException("Provider model discovery response did not contain a 'data' array.");

                return [];
            }

            return [.. dataArray.EnumerateArray()
                .Select(static m => m.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => DiscoveredModelCapabilities.FromModelId(id, id))
                .OrderBy(static model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)];
        }
        catch (HttpRequestException) when (!throwOnFailure) { return []; }
        catch (JsonException) when (!throwOnFailure) { return []; }
        catch (IOException) when (!throwOnFailure) { return []; }
    }
}
