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
    INetworkAccessChecker? checker = null,
    Func<INetworkAccessChecker?, HttpMessageHandler>? transportFactory = null) : IAiClientProvider
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
        HttpMessageHandler transportHandler = transportFactory is null
            ? new NetworkAccessCheckHandler(checker ?? FailClosedNetworkAccessChecker.Instance, new SocketsHttpHandler { AllowAutoRedirect = false })
            : transportFactory(checker) ?? throw new InvalidOperationException("Configured model transport returned no handler.");

        return new OpenAIClient(new ApiKeyCredential(apiKey), OpenAiCompatibleEndpoint.CreateClientOptions(baseUrl, transportHandler))
            .GetChatClient(modelName)
            .AsIChatClient();
#pragma warning restore CA2000
    }

    public async Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
        JsonObject settings,
        string? apiKey,
        bool throwOnFailure = false,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable CA2000 // Passed to DiscoverModelsAsync
        using HttpMessageHandler transportHandler = transportFactory is null
            ? new NetworkAccessCheckHandler(checker ?? FailClosedNetworkAccessChecker.Instance, new SocketsHttpHandler { AllowAutoRedirect = false })
            : transportFactory(checker) ?? throw new InvalidOperationException("Configured model transport returned no handler.");

        return await OpenAiCompatibleEndpoint.DiscoverModelsAsync(
            settings,
            apiKey,
            defaultBaseUrl,
            runLimitResolver,
            throwOnFailure,
            cancellationToken,
            transportHandler).ConfigureAwait(false);
#pragma warning restore CA2000
    }
}
