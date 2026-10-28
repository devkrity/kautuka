using System.Text.Json;
using System.Text.Json.Nodes;

using Google.GenAI;
using Google.GenAI.Types;

using Kautuka.Gateway.Domain.Runtime;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class GoogleAiClientProvider(IRunLimitResolver? runLimitResolver = null) : IAiClientProvider
{
    private const string httpTimeoutSettingKey = "http_timeout_seconds";
    private const string openaiCompatPathSuffix = "/v1beta/openai";
    private const string modelsResourcePrefix = "models/";

    public string ProviderType => "gemini";

    public IChatClient? TryCreate(JsonObject settings, string modelName, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        // Chat clients are long-lived for the agent runtime; GoogleGenAIChatClient.Dispose is a no-op.
#pragma warning disable CA2000
        return new GeminiDeveloperApiChatClient(
            CreateClient(settings, apiKey).AsIChatClient(NormalizeModelId(modelName)));
#pragma warning restore CA2000
    }

    public IEmbeddingGenerator<string, Embedding<float>>? TryCreateEmbeddingGenerator(
        JsonObject settings,
        string modelName,
        string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

#pragma warning disable CA2000
        return CreateClient(settings, apiKey).AsIEmbeddingGenerator(NormalizeModelId(modelName));
#pragma warning restore CA2000
    }

    public async Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
        JsonObject settings,
        string? apiKey,
        bool throwOnFailure = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException("Provider API key is not configured.");
            }

            return [];
        }

        try
        {
            using Client client = CreateClient(settings, apiKey);
            Pager<Model, ListModelsConfig, ListModelsResponse> pager = await client.Models
                .ListAsync(new ListModelsConfig { QueryBase = true }, cancellationToken)
                .ConfigureAwait(false);

            var discovered = new List<DiscoveredModelDefinition>();
            await foreach (Model model in pager.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                string? name = model.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string displayName = string.IsNullOrWhiteSpace(model.DisplayName) ? name : model.DisplayName;
                discovered.Add(model.SupportedActions is { Count: > 0 } actions
                    ? DiscoveredModelCapabilities.FromDeclaredMethods(name, displayName, model.Description, actions)
                    : DiscoveredModelCapabilities.FromModelId(name, displayName, model.Description));
            }

            return [.. discovered
                .DistinctBy(static model => model.Id, StringComparer.Ordinal)
                .OrderBy(static model => model.DisplayName, StringComparer.Ordinal)
                .ThenBy(static model => model.Id, StringComparer.Ordinal)];
        }
        catch (HttpRequestException) when (!throwOnFailure)
        {
            return [];
        }
        catch (IOException) when (!throwOnFailure)
        {
            return [];
        }
        catch (JsonException) when (!throwOnFailure)
        {
            return [];
        }
        catch (ClientError) when (!throwOnFailure)
        {
            return [];
        }
    }

    internal static string NormalizeModelId(string modelName)
    {
        string trimmed = modelName.Trim();
        return trimmed.StartsWith(modelsResourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[modelsResourcePrefix.Length..]
            : trimmed;
    }

    internal static string? ResolveNativeBaseUrl(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        string trimmed = stored.Trim().TrimEnd('/');
        return IsOpenAiCompatibleGeminiUrl(trimmed) ? null : trimmed;
    }

    internal static bool IsOpenAiCompatibleGeminiUrl(string url)
        => url.EndsWith(openaiCompatPathSuffix, StringComparison.OrdinalIgnoreCase);

    private Client CreateClient(JsonObject settings, string apiKey)
    {
        TimeSpan timeout = AiProviderSettings.GetTimeout(
            settings,
            httpTimeoutSettingKey,
            runLimitResolver?.GetModelTimeout() ?? AiProviderSettings.DefaultHttpTimeout);
        var httpOptions = new HttpOptions
        {
            Timeout = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue),
        };
        string? nativeBaseUrl = ResolveNativeBaseUrl(AiProviderSettings.GetString(settings, "base_url"));
        if (!string.IsNullOrWhiteSpace(nativeBaseUrl))
        {
            httpOptions.BaseUrl = nativeBaseUrl;
        }

        return new Client(apiKey: apiKey, httpOptions: httpOptions);
    }
}
