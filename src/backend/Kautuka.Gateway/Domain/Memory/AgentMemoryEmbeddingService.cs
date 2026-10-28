using System.Text;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Memory;

internal sealed partial class AgentMemoryEmbeddingService(
    GatewayState state,
    KautukaAiClientFactory aiClientFactory,
    IAgentDefinitionStore agentDefinitionStore,
    IAgentMemoryEmbeddingQueue queue,
    IPlatformConfigurationProvider configProvider,
    ILogger<AgentMemoryEmbeddingService> logger) : IAgentMemoryEmbeddingService
{
    public bool EnqueuePersistFacts(string targetType, string targetId, IReadOnlyList<GatewayMemoryFact> facts)
    {
        if (!configProvider.GetCurrent().Memory.EmbeddingsEnabled
            || !string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase)
            || facts.Count == 0)
        {
            return false;
        }

        bool enqueuedAny = false;
        foreach (GatewayMemoryFact fact in facts)
        {
            enqueuedAny |= queue.TryEnqueue(new AgentMemoryEmbeddingWork(AgentMemoryEmbeddingOperation.Persist, fact));
        }

        return enqueuedAny;
    }

    public async Task PersistFactsAsync(string targetType, string targetId, IReadOnlyList<GatewayMemoryFact> facts, CancellationToken cancellationToken = default)
    {
        if (!configProvider.GetCurrent().Memory.EmbeddingsEnabled)
        {
            return;
        }

        if (!string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase) || facts.Count == 0)
        {
            return;
        }

        if (!TryResolveAgentRuntime(targetId, out GatewayAgent? agent, out GatewayModel? model, out GatewayProvider? provider, out string? storageLocatorKey))
        {
            return;
        }

        GatewayModel resolvedModel = model!;
        GatewayProvider resolvedProvider = provider!;
        string resolvedStorageLocatorKey = storageLocatorKey!;

        IEmbeddingGenerator<string, Embedding<float>>? generator = await aiClientFactory
            .TryCreateEmbeddingGeneratorAsync(resolvedProvider.Id, resolvedProvider.Type, resolvedProvider.Settings, resolvedModel.ModelName, cancellationToken)
            .ConfigureAwait(false);
        if (generator is null)
        {
            LogEmbeddingGeneratorUnavailable(logger, targetId, resolvedModel.ModelName);
            return;
        }

        try
        {
            string modelKey = BuildModelKey(resolvedProvider.Id, resolvedModel.ModelName);
            var records = new List<AgentMemoryEmbeddingRecord>(facts.Count);
            foreach (GatewayMemoryFact fact in facts)
            {
                string sourceText = BuildEmbeddingSourceText(fact);
                GeneratedEmbeddings<Embedding<float>> generated = await generator
                    .GenerateAsync([sourceText], options: null, cancellationToken)
                    .ConfigureAwait(false);
                if (generated.Count == 0)
                {
                    continue;
                }

                float[] vector = generated[0].Vector.ToArray();
                if (vector.Length == 0)
                {
                    continue;
                }

                records.Add(new AgentMemoryEmbeddingRecord(
                    fact.Id,
                    fact.TargetType,
                    fact.TargetId,
                    modelKey,
                    vector.Length,
                    vector,
                    sourceText,
                    fact.CreatedAt,
                    fact.UpdatedAt));
            }

            if (records.Count > 0)
            {
                agentDefinitionStore.UpsertMemoryEmbeddings(resolvedStorageLocatorKey, records);
                LogEmbeddingsPersisted(logger, records.Count, targetId, modelKey);
            }
        }
        catch (InvalidOperationException ex)
        {
            LogEmbeddingPersistFailed(logger, ex, targetId);
        }
        catch (IOException ex)
        {
            LogEmbeddingPersistFailed(logger, ex, targetId);
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            LogEmbeddingPersistFailed(logger, ex, targetId);
        }
    }

    public Task PersistFactAsync(GatewayMemoryFact fact, CancellationToken cancellationToken = default)
        => PersistFactsAsync(fact.TargetType, fact.TargetId, [fact], cancellationToken);

    public Task DeleteFactAsync(GatewayMemoryFact fact, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!string.Equals(fact.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
            || !state.AgentStorageLocators.TryGetValue(fact.TargetId, out string? storageLocatorKey))
        {
            return Task.CompletedTask;
        }

        agentDefinitionStore.DeleteMemoryEmbedding(storageLocatorKey, fact.Id);
        return Task.CompletedTask;
    }

    private bool TryResolveAgentRuntime(string agentId, out GatewayAgent? agent, out GatewayModel? model, out GatewayProvider? provider, out string? storageLocatorKey)
    {
        if (!state.Agents.TryGetValue(agentId, out agent)
            || !state.Models.TryGetValue(agent.ModelId, out model)
            || !state.Providers.TryGetValue(model.ProviderId, out provider)
            || !state.AgentStorageLocators.TryGetValue(agentId, out storageLocatorKey))
        {
            model = null;
            provider = null;
            storageLocatorKey = null;
            return false;
        }

        return true;
    }

    private static string BuildEmbeddingSourceText(GatewayMemoryFact fact)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(fact.Title))
        {
            builder.AppendLine(fact.Title);
        }

        if (!string.IsNullOrWhiteSpace(fact.Category))
        {
            builder.Append("Category: ");
            builder.AppendLine(fact.Category);
        }

        builder.Append(fact.Content);
        return builder.ToString();
    }

    private static string BuildModelKey(string providerId, string modelName) => $"{providerId}:{modelName}";

    [LoggerMessage(EventId = 160, Level = LogLevel.Information, Message = "Persisted {Count} memory embedding(s) for agent {AgentId} using {ModelKey}")]
    private static partial void LogEmbeddingsPersisted(ILogger logger, int count, string agentId, string modelKey);

    [LoggerMessage(EventId = 161, Level = LogLevel.Warning, Message = "Embedding generator unavailable for agent {AgentId} using model {ModelName}")]
    private static partial void LogEmbeddingGeneratorUnavailable(ILogger logger, string agentId, string modelName);

    [LoggerMessage(EventId = 162, Level = LogLevel.Warning, Message = "Persisting memory embeddings failed for agent {AgentId}")]
    private static partial void LogEmbeddingPersistFailed(ILogger logger, Exception exception, string agentId);
}
