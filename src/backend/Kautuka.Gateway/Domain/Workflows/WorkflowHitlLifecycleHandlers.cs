using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

using Mediator;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Workflows;

internal sealed class WorkflowReviewResolvedHandler(
    IServiceProvider services,
    IGatewayApprovalStore approvalStore,
    IGatewayWorkflowStore workflowStore,
    IWorkflowNaradPublisher naradPublisher) : INotificationHandler<HitlApprovalResolved>
{
    public async ValueTask Handle(HitlApprovalResolved notification, CancellationToken cancellationToken)
    {
        GatewayToolApprovalRequest? request = approvalStore.GetById(notification.RequestId);
        if (request is null
            || string.IsNullOrWhiteSpace(request.InstanceId)
            || (!string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal)
                && !string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal)))
        {
            return;
        }

        string? storedDecision = request.Decision;
        bool wasApproved = string.Equals(storedDecision, ApprovalActionCatalog.Approve, StringComparison.Ordinal)
            || string.Equals(storedDecision, "approved", StringComparison.Ordinal);
        bool wasStopped = string.Equals(storedDecision, ApprovalActionCatalog.StopRun, StringComparison.Ordinal)
            || string.Equals(storedDecision, "stopped", StringComparison.Ordinal);
        if (notification.Source != HitlResolutionSource.SystemTimeout
            && !wasApproved
            && !wasStopped
            && !string.Equals(storedDecision, ApprovalActionCatalog.RequestChanges, StringComparison.Ordinal))
        {
            return;
        }
        string decision = notification.Source == HitlResolutionSource.SystemTimeout
            ? "expired"
            : wasStopped
                ? "stop"
                : wasApproved
                    ? "approve"
                    : "request_changes";
        string waitId = NaradEventBodies.ApprovalWaitId(request.Id);
        GatewayWorkflowInstance? instance = workflowStore.GetInstance(request.InstanceId);
        if (instance is not null)
        {
            string journalDecision = notification.Source == HitlResolutionSource.SystemTimeout
                ? ApprovalActionCatalog.Expired
                : wasStopped
                    ? ApprovalActionCatalog.StopRun
                    : wasApproved
                        ? ApprovalActionCatalog.Approve
                        : ApprovalActionCatalog.RequestChanges;
            naradPublisher.Publish(
                instance,
                NaradEventTypes.InterruptResolved,
                new JsonObject
                {
                    ["interruptId"] = request.Id,
                    ["waitId"] = waitId,
                    ["decision"] = journalDecision,
                    ["scope"] = "workflow",
                    ["userId"] = instance.OwnerUserId,
                });
        }

        IWorkflowExecutionService execution = services.GetRequiredService<IWorkflowExecutionService>();
        await execution.ContinueAsync(request.InstanceId, decision, cancellationToken, waitId).ConfigureAwait(false);
    }
}
