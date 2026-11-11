using System.ClientModel;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Runtime;
using Kautuka.NetworkAccess.Client;

using Microsoft.Extensions.AI;

using OpenAI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class OpenAiCompatibleProvider(
    string providerType,
    string? defaultBaseUrl = null,
    IRunLimitResolver? runLimitResolver = null,
    INetworkAccessChecker? checker = null) : IAiClientProvider
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

#pragma warning disable CA2000 // Owned by chat client
        HttpMessageHandler? transportHandler = checker is not null
            ? new NetworkAccessCheckHandler(checker, new SocketsHttpHandler())
            : null;

        return new OpenAIClient(new ApiKeyCredential(apiKey), OpenAiCompatibleEndpoint.CreateClientOptions(baseUrl, transportHandler))
            .GetChatClient(modelName)
            .AsIChatClient();
#pragma warning restore CA2000
    }

    public Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
        JsonObject settings,
        string? apiKey,
        bool throwOnFailure = false,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable CA2000 // Passed to DiscoverModelsAsync
        HttpMessageHandler? transportHandler = checker is not null
            ? new NetworkAccessCheckHandler(checker, new SocketsHttpHandler())
            : null;

        return OpenAiCompatibleEndpoint.DiscoverModelsAsync(
            settings,
            apiKey,
            defaultBaseUrl,
            runLimitResolver,
            throwOnFailure,
            cancellationToken,
            transportHandler);
#pragma warning restore CA2000
    }
}
