using System.Text.Json;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.ModelCapability;

internal sealed class ModelCapabilityService(
    IModelCapabilityStore store,
    IModelCapabilityProber prober,
    ILogger<ModelCapabilityService> logger)
    : IModelCapabilityService
{
    public async ValueTask<ModelCapabilityProfile> GetOrProbeAsync(
        ModelCapabilityKey key,
        IChatClient rawClient,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        string keyValue = key.ToStoreKey();
        ModelCapabilityProfile? existing = await store.TryGetAsync(key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            bool reasoningMayBeOnlyOutput = existing.GetEffective().ReasoningMayBeOnlyOutput;
            ModelCapabilityServiceLog.CacheHit(
                logger,
                keyValue,
                reasoningMayBeOnlyOutput);
            return existing;
        }

        ModelCapabilityServiceLog.ProbingModel(logger, key.ModelId, key.ProviderId);

        DeclaredCapabilities declared = new();
        ProbedCapabilities probed;
        try
        {
            probed = await prober.ProbeAsync(rawClient, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ModelCapabilityServiceLog.ProbeFailed(logger, keyValue, "timed out");
            probed = new ProbedCapabilities { ProbedAt = DateTimeOffset.UtcNow };
        }
        catch (HttpRequestException ex)
        {
            ModelCapabilityServiceLog.ProbeFailed(logger, keyValue, ex.Message);
            probed = new ProbedCapabilities { ProbedAt = DateTimeOffset.UtcNow };
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            ModelCapabilityServiceLog.ProbeFailed(logger, keyValue, ex.Message);
            probed = new ProbedCapabilities { ProbedAt = DateTimeOffset.UtcNow };
        }
        catch (IOException ex)
        {
            ModelCapabilityServiceLog.ProbeFailed(logger, keyValue, ex.Message);
            probed = new ProbedCapabilities { ProbedAt = DateTimeOffset.UtcNow };
        }
        catch (JsonException ex)
        {
            ModelCapabilityServiceLog.ProbeFailed(logger, keyValue, ex.Message);
            probed = new ProbedCapabilities { ProbedAt = DateTimeOffset.UtcNow };
        }
        catch (InvalidOperationException ex)
        {
            ModelCapabilityServiceLog.ProbeFailed(logger, keyValue, ex.Message);
            probed = new ProbedCapabilities { ProbedAt = DateTimeOffset.UtcNow };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            ModelCapabilityServiceLog.ProbeFailed(logger, keyValue, ex.Message);
            probed = new ProbedCapabilities { ProbedAt = DateTimeOffset.UtcNow };
        }
        catch (NotSupportedException ex)
        {
            ModelCapabilityServiceLog.ProbeFailed(logger, keyValue, ex.Message);
            probed = new ProbedCapabilities { ProbedAt = DateTimeOffset.UtcNow };
        }
        catch (FormatException ex)
        {
            ModelCapabilityServiceLog.ProbeFailed(logger, keyValue, ex.Message);
            probed = new ProbedCapabilities { ProbedAt = DateTimeOffset.UtcNow };
        }

        ModelCapabilityProfile profile = ModelCapabilityProfile.CreateNew(key, declared, probed);
        await store.SaveAsync(profile, ct).ConfigureAwait(false);

        ModelCapabilityServiceLog.ProfileCreated(
            logger,
            keyValue,
            probed.SupportsStreaming,
            probed.SupportsReasoningChannel,
            probed.ReasoningMayBeOnlyOutput);

        return profile;
    }

    public async ValueTask RecordObservationAsync(
        ModelCapabilityKey key,
        RuntimeObservation observation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        ModelCapabilityProfile? profile = await store.TryGetAsync(key, ct).ConfigureAwait(false);
        if (profile is null) return;

        ObservedBehaviors updated = observation switch
        {
            RuntimeObservation.ReasoningWithEmptyText => profile.Observed.WithReasoningEmptyText(),
            RuntimeObservation.NormalRun => profile.Observed.WithNormalRun(),
            RuntimeObservation.StreamingSuccess => profile.Observed.WithStreamingSuccess(),
            _ => profile.Observed.WithNormalRun()
        };

        await store.SaveAsync(profile.WithObservation(updated), ct).ConfigureAwait(false);
    }
}

internal static partial class ModelCapabilityServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Capability profile cache hit for {Key}. ReasoningMayBeOnlyOutput={Flag}")]
    public static partial void CacheHit(ILogger logger, string key, bool flag);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Probing capabilities for model {ModelId} (provider={ProviderId})")]
    public static partial void ProbingModel(ILogger logger, string modelId, string providerId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Capability profile created for {Key}: Streaming={Streaming}, Reasoning={Reasoning}, ReasoningOnly={ReasoningOnly}")]
    public static partial void ProfileCreated(ILogger logger, string key, bool? streaming, bool? reasoning, bool? reasoningOnly);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Capability probe failed for {Key}; continuing with unknown capabilities. Detail: {Detail}")]
    public static partial void ProbeFailed(ILogger logger, string key, string detail);
}
