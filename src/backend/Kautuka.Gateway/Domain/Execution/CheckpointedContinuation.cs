using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Execution;

internal static class ContinuationCheckpointBoundaryTypes
{
    public const string ToolResult = "tool_result";
    public const string ToolApprovalPending = "tool_approval_pending";
    public const string AgentToolApprovalPending = "agent_tool_approval_pending";
    public const string AssistantTurnCompleted = "assistant_turn_completed";
}

internal sealed record AgentContinuationRequest(
    bool Enabled,
    string? ResumeToken,
    string? ContinuationRunId = null,
    string? CheckpointId = null,
    int? CheckpointIndex = null,
    string? SessionStateJson = null,
    string? EffectiveBehaviorJson = null,
    string? BoundaryType = null,
    IReadOnlyList<ContinuationArtifactManifestEntry>? ArtifactManifest = null,
    IReadOnlyList<GatewayToolCall>? PriorToolCalls = null,
    TimeSpan ActiveElapsed = default);

internal sealed record ContinuationCheckpointCreateRequest(
    string ContinuationRunId,
    string SessionId,
    string AgentId,
    string? UserId,
    string RunId,
    string BoundaryType,
    string SessionStateJson,
    string EffectiveBehaviorJson,
    IReadOnlyList<ContinuationArtifactManifestEntry> ArtifactManifest,
    int CheckpointIndex);

internal sealed record GatewayContinuationCheckpoint(
    string Id,
    string ContinuationRunId,
    int CheckpointIndex,
    string SessionId,
    string AgentId,
    string? UserId,
    string RunId,
    string BoundaryType,
    string SessionStateJson,
    string EffectiveBehaviorJson,
    string ArtifactManifestJson,
    string ResumeTokenHash,
    string CreatedAt);

internal sealed record IssuedCheckpointedContinuationResult(
    string CheckpointId,
    string ContinuationRunId,
    int CheckpointIndex,
    string ResumeToken,
    string BoundaryType,
    string CreatedAt,
    IReadOnlyList<ContinuationArtifactManifestEntry> ArtifactManifest);

internal sealed record ResumeTokenValidationResult(
    bool IsValid,
    string? ContinuationRunId,
    string? FailureReason,
    string? CheckpointId = null,
    int? CheckpointIndex = null,
    string? SessionId = null,
    string? AgentId = null,
    string? UserId = null,
    string? RunId = null,
    string? SessionStateJson = null,
    string? EffectiveBehaviorJson = null,
    string? BoundaryType = null,
    IReadOnlyList<ContinuationArtifactManifestEntry>? ArtifactManifest = null);

internal interface IResumeTokenProvider
{
    ValueTask<IssuedCheckpointedContinuationResult> IssueCheckpointAsync(ContinuationCheckpointCreateRequest request, CancellationToken cancellationToken);

    ValueTask<ResumeTokenValidationResult> ValidateAsync(string resumeToken, CancellationToken cancellationToken);
}

internal sealed class ResumeTokenProvider(IGatewayContinuationStore continuationStore) : IResumeTokenProvider
{
    public ValueTask<IssuedCheckpointedContinuationResult> IssueCheckpointAsync(ContinuationCheckpointCreateRequest request, CancellationToken cancellationToken)
    {
        string resumeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string checkpointId = $"checkpoint-{Guid.NewGuid():N}";
        int checkpointIndex = Math.Max(request.CheckpointIndex, continuationStore.GetNextCheckpointIndex(request.ContinuationRunId));
        string createdAt = DateTimeOffset.UtcNow.ToString("O");
        string artifactManifestJson = JsonSerializer.Serialize([.. request.ArtifactManifest], KautukaJsonContext.Default.ListContinuationArtifactManifestEntry);

        continuationStore.CreateCheckpoint(new GatewayContinuationCheckpoint(
            checkpointId,
            request.ContinuationRunId,
            checkpointIndex,
            request.SessionId,
            request.AgentId,
            request.UserId,
            request.RunId,
            request.BoundaryType,
            request.SessionStateJson,
            request.EffectiveBehaviorJson,
            artifactManifestJson,
            ComputeTokenHash(resumeToken),
            createdAt));

        return ValueTask.FromResult(new IssuedCheckpointedContinuationResult(
            checkpointId,
            request.ContinuationRunId,
            checkpointIndex,
            resumeToken,
            request.BoundaryType,
            createdAt,
            request.ArtifactManifest));
    }

    public ValueTask<ResumeTokenValidationResult> ValidateAsync(string resumeToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resumeToken))
        {
            return ValueTask.FromResult(new ResumeTokenValidationResult(false, null, "Resume token is required."));
        }

        GatewayContinuationCheckpoint? checkpoint = continuationStore.GetByResumeTokenHash(ComputeTokenHash(resumeToken));
        if (checkpoint is null)
        {
            return ValueTask.FromResult(new ResumeTokenValidationResult(false, null, "Resume token is invalid or has expired."));
        }

        List<ContinuationArtifactManifestEntry> artifactManifest = JsonSerializer.Deserialize(checkpoint.ArtifactManifestJson, KautukaJsonContext.Default.ListContinuationArtifactManifestEntry) ?? [];
        return ValueTask.FromResult(new ResumeTokenValidationResult(
            true,
            checkpoint.ContinuationRunId,
            null,
            checkpoint.Id,
            checkpoint.CheckpointIndex,
            checkpoint.SessionId,
            checkpoint.AgentId,
            checkpoint.UserId,
            checkpoint.RunId,
            checkpoint.SessionStateJson,
            checkpoint.EffectiveBehaviorJson,
            checkpoint.BoundaryType,
            artifactManifest));
    }

    private static string ComputeTokenHash(string resumeToken)
    {
        byte[] tokenBytes = Encoding.UTF8.GetBytes(resumeToken);
        byte[] hash = SHA256.HashData(tokenBytes);
        return Convert.ToHexString(hash);
    }
}
