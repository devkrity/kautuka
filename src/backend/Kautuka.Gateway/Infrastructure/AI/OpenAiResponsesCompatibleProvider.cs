using System.ClientModel;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Runtime;

using Microsoft.Extensions.AI;

using OpenAI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class OpenAiResponsesCompatibleProvider(
    IRunLimitResolver? runLimitResolver = null,
    string? defaultBaseUrl = null,
    HttpMessageHandler? transportHandler = null) : IAiClientProvider
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

#pragma warning disable OPENAI001
        IChatClient inner = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            OpenAiCompatibleEndpoint.CreateClientOptions(baseUrl, transportHandler))
            .GetResponsesClient()
            .AsIChatClient(modelName);
#pragma warning restore OPENAI001

        return new StatelessOpenAiResponsesChatClient(inner);
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
