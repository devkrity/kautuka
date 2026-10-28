namespace Kautuka.Gateway.Domain.Memory;

using Microsoft.Extensions.AI;

internal interface IChatMemoryAssembler
{
    Task<IReadOnlyList<InjectedMemoryFact>> AssembleAsync(string targetType, string targetId, string? queryText, string? actingUserId, CancellationToken cancellationToken = default);
}

internal sealed partial class ChatMemoryAssembler(
    IGatewayMemoryStore memoryStore,
    IMemoryScopePolicy scopePolicy,
    IPlatformConfigurationProvider configProvider,
    GatewayState state,
    KautukaAiClientFactory aiClientFactory,
    IAgentDefinitionStore agentDefinitionStore,
    ILogger<ChatMemoryAssembler> logger) : IChatMemoryAssembler
{
    private const int maxFacts = 40;
    private const int maxVectorFacts = 12;

    public async Task<IReadOnlyList<InjectedMemoryFact>> AssembleAsync(string targetType, string targetId, string? queryText, string? actingUserId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GatewayMemoryScope> scopes = scopePolicy.GetReadScopes(targetType, targetId);
        double injectThreshold = configProvider.GetCurrent().Memory.FactInjectThreshold;

        var facts = new List<InjectedMemoryFact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, GatewayMemoryFact> rankedFacts = await RankFactsAsync(targetType, targetId, queryText, actingUserId, scopes, cancellationToken).ConfigureAwait(false);

        foreach (GatewayMemoryScope scope in scopes)
        {
            IReadOnlyList<GatewayMemoryFact> scopedFacts = rankedFacts.Count > 0
                ? [.. rankedFacts.Values.Where(fact => string.Equals(fact.TargetType, scope.TargetType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(fact.TargetId, scope.TargetId, StringComparison.Ordinal))]
                : memoryStore.List(scope.TargetType, scope.TargetId);

            foreach (GatewayMemoryFact fact in scopedFacts)
            {
                if (!BelongsToActingUser(fact, actingUserId) || fact.Confidence < injectThreshold)
                    continue;

                var injected = new InjectedMemoryFact(
                    SourceScope: scope.TargetType,
                    SourceId: scope.TargetId,
                    Title: fact.Title,
                    Content: fact.Content,
                    Category: fact.Category,
                    UpdatedAt: fact.UpdatedAt);

                string dedupeKey = BuildDedupeKey(injected);
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                facts.Add(injected);
            }
        }

        return facts
            .OrderBy(static fact => GetScopeRank(fact.SourceScope))
            .ThenBy(fact => fact.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(GetSortTimestamp)
            .Take(maxFacts)
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, GatewayMemoryFact>> RankFactsAsync(
        string targetType,
        string targetId,
        string? queryText,
        string? actingUserId,
        IReadOnlyList<GatewayMemoryScope> scopes,
        CancellationToken cancellationToken)
    {
        if (!IsEmbeddingRankingEnabled(configProvider.GetCurrent().Memory.EmbeddingsEnabled, targetType, queryText)
            || !string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase)
            || !state.Agents.TryGetValue(targetId, out GatewayAgent? agent)
            || !state.Models.TryGetValue(agent.ModelId, out GatewayModel? model)
            || !state.Providers.TryGetValue(model.ProviderId, out GatewayProvider? provider)
            || !state.AgentStorageLocators.TryGetValue(targetId, out string? storageLocatorKey))
        {
            return new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        }

        IEmbeddingGenerator<string, Embedding<float>>? generator = await aiClientFactory
            .TryCreateEmbeddingGeneratorAsync(provider.Id, provider.Type, provider.Settings, model.ModelName, cancellationToken)
            .ConfigureAwait(false);
        if (generator is null)
        {
            LogEmbeddingRetrievalUnavailable(logger, targetId, model.ModelName);
            return new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        }

        GeneratedEmbeddings<Embedding<float>> generated;
        try
        {
            generated = await generator
                .GenerateAsync([queryText!], options: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            LogEmbeddingRetrievalFailed(logger, ex, targetId, model.ModelName);
            return new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        }
        catch (InvalidOperationException ex)
        {
            LogEmbeddingRetrievalFailed(logger, ex, targetId, model.ModelName);
            return new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        }
        catch (IOException ex)
        {
            LogEmbeddingRetrievalFailed(logger, ex, targetId, model.ModelName);
            return new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (IsRecoverableEmbeddingFailure(ex, cancellationToken))
        {
            LogEmbeddingRetrievalFailed(logger, ex, targetId, model.ModelName);
            return new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        }

        if (generated.Count == 0)
        {
            return new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        }

        string modelKey = $"{provider.Id}:{model.ModelName}";
        IReadOnlyList<AgentMemorySearchHit> hits = agentDefinitionStore.SearchMemoryEmbeddings(
            storageLocatorKey,
            modelKey,
            generated[0].Vector.Span,
            maxVectorFacts);
        if (hits.Count == 0)
        {
            return new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        }

        var factsById = new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        foreach (GatewayMemoryScope scope in scopes)
        {
            foreach (GatewayMemoryFact fact in memoryStore.List(scope.TargetType, scope.TargetId))
            {
                if (BelongsToActingUser(fact, actingUserId))
                {
                    factsById[fact.Id] = fact;
                }
            }
        }

        var ranked = new Dictionary<string, GatewayMemoryFact>(StringComparer.Ordinal);
        foreach (AgentMemorySearchHit hit in hits)
        {
            if (factsById.TryGetValue(hit.FactId, out GatewayMemoryFact? fact))
            {
                ranked[hit.FactId] = fact;
            }
        }

        return ranked;
    }

    private static bool BelongsToActingUser(GatewayMemoryFact fact, string? actingUserId)
        => !string.IsNullOrWhiteSpace(actingUserId)
            && string.Equals(fact.OwnerUserId, actingUserId, StringComparison.Ordinal);

    internal static bool IsEmbeddingRankingEnabled(bool embeddingsEnabled, string targetType, string? queryText)
        => embeddingsEnabled
            && !string.IsNullOrWhiteSpace(queryText)
            && string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase);

    internal static bool IsRecoverableEmbeddingFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            OperationCanceledException => true,
            TimeoutException => true,
            System.Net.Http.HttpRequestException => true,
            AggregateException aggregate when aggregate.InnerExceptions.Count > 0
                => aggregate.InnerExceptions.All(inner => IsRecoverableEmbeddingFailure(inner, cancellationToken)),
            _ => false,
        };
    }

    private static string BuildDedupeKey(InjectedMemoryFact fact)
        => string.Join("|",
            fact.SourceScope,
            fact.SourceId,
            fact.Category,
            fact.Title,
            fact.Content);

    private static int GetScopeRank(string sourceScope)
    {
        _ = sourceScope;
        return 0;
    }

    private static DateTimeOffset GetSortTimestamp(InjectedMemoryFact fact)
        => DateTimeOffset.TryParse(fact.UpdatedAt, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    [LoggerMessage(EventId = 163, Level = LogLevel.Debug, Message = "Embedding retrieval unavailable for agent {AgentId} using model {ModelName}; falling back to non-vector memory injection")]
    private static partial void LogEmbeddingRetrievalUnavailable(ILogger logger, string agentId, string modelName);

    [LoggerMessage(EventId = 164, Level = LogLevel.Warning, Message = "Embedding retrieval failed for agent {AgentId} using model {ModelName}; falling back to non-vector memory injection")]
    private static partial void LogEmbeddingRetrievalFailed(ILogger logger, Exception exception, string agentId, string modelName);
}
