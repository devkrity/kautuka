using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Workflows;

internal static class WorkflowReviewNotes
{
    public static string? ForAttempt(
        GatewayWorkflowInstance instance,
        GatewayWorkflowStepAttempt attempt,
        IGatewayApprovalStore approvalStore)
    {
        GatewayWorkflowStepAttempt? lastFinished = instance.Steps
            .Where(step => string.Equals(step.NodeId, attempt.NodeId, StringComparison.Ordinal)
                && step.Attempt < attempt.Attempt
                && string.Equals(step.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal))
            .OrderByDescending(step => step.Attempt)
            .FirstOrDefault();
        if (lastFinished is null)
        {
            return null;
        }

        GatewayToolApprovalRequest? review = approvalStore.GetDecidedByRunAndTool(
            lastFinished.RunId,
            $"{instance.Id}:{attempt.NodeId}:{lastFinished.Attempt}",
            "workflow_review");
        return string.IsNullOrWhiteSpace(review?.Reason)
            || string.Equals(review.Reason, ApprovalActionCatalog.RequestChanges, StringComparison.Ordinal)
                ? null
                : review.Reason;
    }
}
