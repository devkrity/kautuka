using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Kautuka.Gateway.Domain.Runtime;

using Microsoft.Extensions.AI;

using OllamaSharp;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class OllamaAiClientProvider(IRunLimitResolver? runLimitResolver = null, string? defaultBaseUrl = null) : IAiClientProvider
{
    private const string httpTimeoutSettingKey = "http_timeout_seconds";

    /// <summary>
    /// OllamaSharp ships an internal source-gen context that includes
    /// <c>OllamaFunctionResultContent</c> (needed when mapping tool results). Our public
    /// mirror cannot reference that internal type, so we resolve theirs at runtime.
    /// </summary>
    private static readonly JsonSerializerContext ollamaJsonContext = ResolveOllamaSharpJsonContext();

    public string ProviderType => "ollama";

    private string? ResolveBaseUrl(JsonObject settings)
    {
        string? stored = AiProviderSettings.GetString(settings, "base_url");
        return string.IsNullOrWhiteSpace(stored) ? defaultBaseUrl : stored;
    }

    public IChatClient? TryCreate(JsonObject settings, string modelName, string? apiKey)
    {
        _ = apiKey;
        string? baseUrl = ResolveBaseUrl(settings);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        // Native Ollama /api/chat via OllamaSharp so thinking maps to TextReasoningContent
        // and tool calls map to FunctionCallContent for MAF — without the OpenAI /v1 shim
        // that drops delta.reasoning.
        return CreateClient(baseUrl, modelName, settings);
    }

    public IEmbeddingGenerator<string, Embedding<float>>? TryCreateEmbeddingGenerator(
        JsonObject settings,
        string modelName,
        string? apiKey)
    {
        _ = apiKey;
        string? baseUrl = ResolveBaseUrl(settings);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return CreateClient(baseUrl, modelName, settings);
    }

    public async Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
        JsonObject settings,
        string? apiKey,
        bool throwOnFailure = false,
        CancellationToken cancellationToken = default)
    {
        _ = apiKey;
        string? baseUrl = ResolveBaseUrl(settings);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException("Provider base URL is not configured.");
            }

            return [];
        }

        try
        {
            using var http = new HttpClient
            {
                Timeout = ResolveHttpTimeout(settings),
            };
            using HttpResponseMessage response = await http.GetAsync(
                new Uri($"{baseUrl.TrimEnd('/')}/api/tags", UriKind.Absolute), cancellationToken).ConfigureAwait(false);
            if (throwOnFailure)
                response.EnsureSuccessStatusCode();
            else if (!response.IsSuccessStatusCode)
                return [];

            using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!payload.RootElement.TryGetProperty("models", out JsonElement modelsArray)
                || modelsArray.ValueKind != JsonValueKind.Array)
            {
                if (throwOnFailure)
                    throw new JsonException("Ollama model discovery response did not contain a 'models' array.");

                return [];
            }

            return [.. modelsArray.EnumerateArray()
                .Select(static m => m.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty)
                .Where(static name => !string.IsNullOrEmpty(name))
                .Select(static name => DiscoveredModelCapabilities.FromModelId(name, name))];
        }
        catch (HttpRequestException) when (!throwOnFailure)
        {
            return [];
        }
        catch (JsonException) when (!throwOnFailure)
        {
            return [];
        }
        catch (IOException) when (!throwOnFailure)
        {
            return [];
        }
    }

    public async Task<(DeclaredCapabilities Declared, string? ModelDigest)> IntrospectModelAsync(
        JsonObject settings, string modelName, string? apiKey, CancellationToken ct = default)
    {
        _ = apiKey;
        string? baseUrl = ResolveBaseUrl(settings);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (new DeclaredCapabilities(), null);
        }

        using var http = new HttpClient
        {
            Timeout = ResolveHttpTimeout(settings),
        };
        string body = JsonSerializer.Serialize(new OllamaShowRequest(modelName), KautukaJsonContext.Default.OllamaShowRequest);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.PostAsync(
            new Uri($"{baseUrl.TrimEnd('/')}/api/show", UriKind.Absolute), content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (new DeclaredCapabilities(), null);

        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        JsonElement root = payload.RootElement;

        string? digest = root.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
        string? family = root.TryGetProperty("details", out JsonElement details)
            && details.TryGetProperty("family", out JsonElement f)
            ? f.GetString()
            : null;

        // Ollama doesn't declare thinking capability via API; we detect it from model family name.
        bool isThinkingModel = IsKnownThinkingFamily(family, modelName);

        return (new DeclaredCapabilities
        {
            ModelFamily = family,
            ModelDigest = digest,
            IsThinkingModel = isThinkingModel ? true : null,
            SupportsReasoningChannel = isThinkingModel ? true : null,
        }, digest);
    }

    private OllamaApiClient CreateClient(string baseUrl, string modelName, JsonObject settings)
    {
        // Chat clients are long-lived for the agent runtime; OllamaApiClient does not take
        // ownership of an externally supplied HttpClient (_disposeHttpClient stays false).
#pragma warning disable CA2000
        var http = new HttpClient
        {
            BaseAddress = new Uri(EnsureTrailingSlash(baseUrl)),
            Timeout = ResolveHttpTimeout(settings),
        };
        return new OllamaApiClient(http, modelName, ollamaJsonContext);
#pragma warning restore CA2000
    }

    private TimeSpan ResolveHttpTimeout(JsonObject settings)
        => AiProviderSettings.GetTimeout(
            settings,
            httpTimeoutSettingKey,
            runLimitResolver?.GetModelTimeout() ?? AiProviderSettings.DefaultHttpTimeout);

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Resolves OllamaSharp's shipped JsonSourceGenerationContext by well-known type name; rooted via DynamicDependency.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "Default property access is rooted via DynamicDependency on PublicProperties.")]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicProperties,
        "OllamaSharp.Models.JsonSourceGenerationContext",
        "OllamaSharp")]
    private static JsonSerializerContext ResolveOllamaSharpJsonContext()
    {
        // Prefer Type.GetType (assembly-qualified) over Assembly.GetType for the trimmer.
        Type? contextType = Type.GetType(
            "OllamaSharp.Models.JsonSourceGenerationContext, OllamaSharp",
            throwOnError: false);
        PropertyInfo? defaultProperty = contextType?.GetProperty(
            "Default",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (defaultProperty?.GetValue(null) is JsonSerializerContext context)
        {
            return context;
        }

        // Fallback: incomplete public mirror (missing internal OllamaFunctionResultContent).
        return OllamaSharpAotJsonContext.Default;
    }

    private static string EnsureTrailingSlash(string baseUrl)
    {
        string trimmed = baseUrl.TrimEnd('/');
        return trimmed + "/";
    }

    private static bool IsKnownThinkingFamily(string? family, string modelName)
    {
        // Conservative: only flag well-known reasoning model families.
        // Does not hardcode specific model IDs — uses family metadata from Ollama.
        string[] thinkingFamilies = ["deepseek-r1", "deepseek", "qwq", "qwen3", "gemma", "phi4", "openthinker", "nemotron"];
        if (family is not null)
        {
            foreach (string f in thinkingFamilies)
                if (family.Contains(f, StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        // Fallback: model name contains reasoning-indicative suffixes
        foreach (string f in thinkingFamilies)
            if (modelName.Contains(f, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
