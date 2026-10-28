using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.IntegrationTests;

public sealed class ResumeTokenProviderTests
{
    [Fact]
    public async Task IssueAndValidateRoundTripsEffectiveBehaviorSnapshot()
    {
        var store = new FakeGatewayContinuationStore();
        var provider = new ResumeTokenProvider(store);
        EffectiveRunBehavior behavior = CreateBehavior();

        IssuedCheckpointedContinuationResult issued = await provider.IssueCheckpointAsync(
            new ContinuationCheckpointCreateRequest(
                ContinuationRunId: "cont-1",
                SessionId: "session-1",
                AgentId: "agent-1",
                UserId: "user-1",
                RunId: "run-1",
                BoundaryType: ContinuationCheckpointBoundaryTypes.AssistantTurnCompleted,
                SessionStateJson: "{}",
                EffectiveBehaviorJson: JsonSerializer.Serialize(behavior, KautukaJsonContext.Default.EffectiveRunBehavior),
                ArtifactManifest: [],
                CheckpointIndex: 2),
            CancellationToken.None);

        ResumeTokenValidationResult validated = await provider.ValidateAsync(issued.ResumeToken, CancellationToken.None);

        Assert.True(validated.IsValid);
        Assert.NotNull(validated.EffectiveBehaviorJson);

        EffectiveRunBehavior? roundTripped = JsonSerializer.Deserialize(validated.EffectiveBehaviorJson, KautukaJsonContext.Default.EffectiveRunBehavior);
        Assert.NotNull(roundTripped);
        Assert.Equal("agent-1", roundTripped.AgentId);
        Assert.Equal(0.5f, roundTripped.SamplingPolicy.Temperature);
        Assert.Equal("tool-a", Assert.Single(roundTripped.Tooling.ToolNames));
    }

    [Fact]
    public async Task IssueCheckpointUsesNextPersistedIndexWhenRequestedIndexWouldRewind()
    {
        var store = new FakeGatewayContinuationStore();
        var provider = new ResumeTokenProvider(store);
        EffectiveRunBehavior behavior = CreateBehavior();

        await provider.IssueCheckpointAsync(
            new ContinuationCheckpointCreateRequest(
                ContinuationRunId: "cont-approval-resume",
                SessionId: "session-1",
                AgentId: "agent-1",
                UserId: "user-1",
                RunId: "run-before-approval",
                BoundaryType: ContinuationCheckpointBoundaryTypes.ToolResult,
                SessionStateJson: "{}",
                EffectiveBehaviorJson: JsonSerializer.Serialize(behavior, KautukaJsonContext.Default.EffectiveRunBehavior),
                ArtifactManifest: [],
                CheckpointIndex: 0),
            CancellationToken.None);

        IssuedCheckpointedContinuationResult resumed = await provider.IssueCheckpointAsync(
            new ContinuationCheckpointCreateRequest(
                ContinuationRunId: "cont-approval-resume",
                SessionId: "session-1",
                AgentId: "agent-1",
                UserId: "user-1",
                RunId: "run-after-approval",
                BoundaryType: ContinuationCheckpointBoundaryTypes.AssistantTurnCompleted,
                SessionStateJson: "{}",
                EffectiveBehaviorJson: JsonSerializer.Serialize(behavior, KautukaJsonContext.Default.EffectiveRunBehavior),
                ArtifactManifest: [],
                CheckpointIndex: 0),
            CancellationToken.None);

        Assert.Equal(1, resumed.CheckpointIndex);
    }

    private static EffectiveRunBehavior CreateBehavior()
        => new(
            AgentId: "agent-1",
            AgentName: "Office Agent",
            ProviderId: "provider-1",
            ProviderType: "OpenAICompatible",
            ProviderBaseUrl: "http://localhost:11434/",
            ModelId: "model-1",
            ModelName: "gemma",
            Persona: new EffectivePersonaProfile("professional", "neutral", "concise"),
            RuntimeGrounding: new EffectiveRuntimeGrounding(
                "2026-05-19T12:00:00.0000000+00:00",
                "Monday, May 19, 2026 12:00:00 PM",
                "en-US",
                "UTC"),
            SamplingPolicy: new EffectiveSamplingPolicy(0.5f, 0.95f),
            SystemContextSections:
            [
                new EffectiveSystemContextSection("systemPrompt", "Base prompt"),
                new EffectiveSystemContextSection("memory", "Memory section")
            ],
            CombinedSystemContext: "Base prompt\n\nMemory section",
            SerializedModelVisibleContext: "{\"persona\":{\"style\":\"professional\",\"tone\":\"neutral\",\"verbosity\":\"concise\"},\"runtimeGrounding\":{\"currentUtc\":\"2026-05-19T12:00:00.0000000+00:00\",\"currentLocal\":\"Monday, May 19, 2026 12:00:00 PM\",\"locale\":\"en-US\",\"timeZone\":\"UTC\"}}",
            SkillAdvertisingContext: string.Empty,
            ScopeContractContext: "Stay in scope.",
            MemoryContext: "Memory section",
            MemoryFactCount: 1,
            Tooling: new EffectiveToolingSnapshot(["tool-a"], "Auto", true));

    private sealed class FakeGatewayContinuationStore : IGatewayContinuationStore
    {
        private readonly Dictionary<string, GatewayContinuationCheckpoint> byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GatewayContinuationCheckpoint> byHash = new(StringComparer.Ordinal);

        public void CreateCheckpoint(GatewayContinuationCheckpoint checkpoint)
        {
            byId[checkpoint.Id] = checkpoint;
            byHash[checkpoint.ResumeTokenHash] = checkpoint;
        }

        public int GetNextCheckpointIndex(string continuationRunId)
            => byId.Values
                .Where(checkpoint => string.Equals(checkpoint.ContinuationRunId, continuationRunId, StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.CheckpointIndex + 1)
                .DefaultIfEmpty(0)
                .Max();

        public GatewayContinuationCheckpoint? GetById(string checkpointId)
            => byId.TryGetValue(checkpointId, out GatewayContinuationCheckpoint? checkpoint) ? checkpoint : null;

        public GatewayContinuationCheckpoint? GetByResumeTokenHash(string resumeTokenHash)
            => byHash.TryGetValue(resumeTokenHash, out GatewayContinuationCheckpoint? checkpoint) ? checkpoint : null;

        public void DeleteBySessions(IReadOnlyCollection<string> sessionIds)
            => throw new NotSupportedException();
    }
}
