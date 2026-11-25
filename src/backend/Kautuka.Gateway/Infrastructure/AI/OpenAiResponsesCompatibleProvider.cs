using System.ClientModel;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Runtime;
using Kautuka.NetworkAccess.Client;

using Microsoft.Extensions.AI;

using OpenAI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class OpenAiResponsesCompatibleProvider(
    IRunLimitResolver? runLimitResolver = null,
    string? defaultBaseUrl = null,
    HttpMessageHandler? transportHandler = null,
    INetworkAccessChecker? checker = null,
    Func<INetworkAccessChecker?, HttpMessageHandler>? transportFactory = null) : IAiClientProvider
{
    public string ProviderType => "openai-responses";

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
        HttpMessageHandler effectiveHandler = transportHandler ?? (transportFactory is null
            ? new NetworkAccessCheckHandler(checker ?? FailClosedNetworkAccessChecker.Instance, new SocketsHttpHandler { AllowAutoRedirect = false })
            : transportFactory(checker) ?? throw new InvalidOperationException("Configured model transport returned no handler."));

#pragma warning disable OPENAI001
        IChatClient inner = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            OpenAiCompatibleEndpoint.CreateClientOptions(baseUrl, effectiveHandler))
            .GetResponsesClient()
            .AsIChatClient(modelName);
#pragma warning restore OPENAI001

        return new StatelessOpenAiResponsesChatClient(inner);
#pragma warning restore CA2000
    }

    public async Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
        JsonObject settings,
        string? apiKey,
        bool throwOnFailure = false,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable CA2000 // Passed to DiscoverModelsAsync
        HttpMessageHandler effectiveHandler = transportHandler ?? (transportFactory is null
            ? new NetworkAccessCheckHandler(checker ?? FailClosedNetworkAccessChecker.Instance, new SocketsHttpHandler { AllowAutoRedirect = false })
            : transportFactory(checker) ?? throw new InvalidOperationException("Configured model transport returned no handler."));

        try
        {
            return await OpenAiCompatibleEndpoint.DiscoverModelsAsync(
                settings,
                apiKey,
                defaultBaseUrl,
                runLimitResolver,
                throwOnFailure,
                cancellationToken,
                effectiveHandler).ConfigureAwait(false);
        }
        finally
        {
            if (transportHandler is null) effectiveHandler.Dispose();
        }
#pragma warning restore CA2000
    }
}
