using System.ClientModel;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Runtime;

using Microsoft.Extensions.AI;

using OpenAI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class OpenAiCompatibleProvider(string providerType, string? defaultBaseUrl = null, IRunLimitResolver? runLimitResolver = null) : IAiClientProvider
{
    public string ProviderType { get; } = providerType;

    public IChatClient? TryCreate(JsonObject settings, string modelName, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        string? baseUrl = OpenAiCompatibleEndpoint.ResolveBaseUrl(settings, defaultBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), OpenAiCompatibleEndpoint.CreateClientOptions(baseUrl, transportHandler: null))
            .GetChatClient(modelName)
            .AsIChatClient();
    }

    public Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
        JsonObject settings,
        string? apiKey,
        bool throwOnFailure = false,
        CancellationToken cancellationToken = default)
        => OpenAiCompatibleEndpoint.DiscoverModelsAsync(
            settings,
            apiKey,
            defaultBaseUrl,
            runLimitResolver,
            throwOnFailure,
            cancellationToken);
}
