using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

/// <summary>
/// Packed-turn approval pairing: FICC needs one response per TARC before it can
/// concurrent-invoke the pack. Human leftovers are never auto-approved by name.
/// </summary>
internal static class PackedToolApproval
{
    public static RunWaitState? FindActiveWait(
        IRunWaitStateStore? store,
        string requestId,
        string? continuationRunId)
    {
        if (store is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(continuationRunId))
        {
            RunWaitState? byContinuation = store.GetActiveByContinuationRunId(continuationRunId);
            if (byContinuation is not null)
            {
                return byContinuation;
            }
        }

        RunWaitState? byCorrelation = store.GetActiveByCorrelationId(requestId);
        if (byCorrelation is not null)
        {
            return byCorrelation;
        }

        foreach (RunWaitState wait in store.ListRecoverable())
        {
            if (!string.Equals(wait.WaitReason, RunWaitReasons.Approval, StringComparison.Ordinal))
            {
                continue;
            }

            if (WaitCoversRequest(wait, requestId))
            {
                return wait;
            }
        }

        return null;
    }

    public static bool WaitCoversRequest(RunWaitState wait, string requestId)
    {
        if (string.Equals(wait.CorrelationId, requestId, StringComparison.Ordinal)
            || string.Equals(wait.ApprovalRequestId, requestId, StringComparison.Ordinal))
        {
            return true;
        }

        return RunClockEnvelope.TryReadPackedApprovalRequestIds(wait.BehaviorJson, out IReadOnlyList<string> packedIds)
            && packedIds.Contains(requestId, StringComparer.Ordinal);
    }

    public static bool HasUndecidedHumanSiblings(
        IGatewayApprovalStore store,
        IReadOnlyList<string> packedRequestIds,
        string resolvedRequestId)
    {
        foreach (string requestId in packedRequestIds)
        {
            if (string.Equals(requestId, resolvedRequestId, StringComparison.Ordinal))
            {
                continue;
            }

            GatewayToolApprovalRequest? row = store.GetById(requestId);
            if (row is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Decision))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> ReadPackedRequestIds(RunWaitState? wait, string fallbackRequestId)
    {
        if (wait is not null
            && RunClockEnvelope.TryReadPackedApprovalRequestIds(wait.BehaviorJson, out IReadOnlyList<string> packedIds)
            && packedIds.Count > 0)
        {
            return packedIds;
        }

        return string.IsNullOrWhiteSpace(fallbackRequestId) ? [] : [fallbackRequestId];
    }

    public static string ResolveJournalInterruptId(RunWaitState? wait, string fallbackRequestId)
    {
        if (wait is not null)
        {
            if (!string.IsNullOrWhiteSpace(wait.ApprovalRequestId))
            {
                return wait.ApprovalRequestId;
            }

            if (!string.IsNullOrWhiteSpace(wait.CorrelationId))
            {
                return wait.CorrelationId;
            }
        }

        return fallbackRequestId;
    }

    public static string ResolveJournalWaitId(RunWaitState? wait, string fallbackRequestId)
        => NaradEventBodies.ApprovalWaitId(ResolveJournalInterruptId(wait, fallbackRequestId));

    public static JsonElement ToInterruptRequestedInput(GatewayChatApprovalRequest approval)
    {
        var input = new JsonObject
        {
            ["toolCallId"] = approval.ToolCallId,
            ["toolName"] = approval.ToolName,
            ["args"] = ParseArgsObject(approval.ArgumentsJson),
        };
        if (approval.Members is not { Count: > 1 })
        {
            return SerializeInput(input);
        }

        input["members"] = new JsonArray(
        [
            .. approval.Members.Select(static member => (JsonNode?)new JsonObject
            {
                ["requestId"] = member.RequestId,
                ["toolCallId"] = member.ToolCallId,
                ["toolName"] = member.ToolName,
                ["toolType"] = member.ToolType,
                ["args"] = ParseArgsObject(member.ArgumentsJson),
            }),
        ]);
        return SerializeInput(input);
    }

    private static JsonObject ParseArgsObject(string? argumentsJson)
        => string.IsNullOrWhiteSpace(argumentsJson)
            ? new JsonObject()
            : JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();

    private static JsonElement SerializeInput(JsonObject input)
        => JsonSerializer.SerializeToElement(input, KautukaJsonContext.Default.JsonObject);

    public static bool CanExecuteIndependently(string? toolName)
        => !ActionIdentity.IsAgentToolInvoke(toolName);

    public static string CreateOutcomeResultJson(string toolName, string outcome, string? reason = null)
    {
        string message = outcome switch
        {
            "denied" => $"Tool '{toolName}' was denied by the user.",
            "expired" => $"Tool '{toolName}' expired without a decision.",
            "error" => string.IsNullOrWhiteSpace(reason)
                ? $"Tool '{toolName}' failed."
                : reason,
            _ => string.IsNullOrWhiteSpace(reason) ? $"Tool '{toolName}' completed." : reason,
        };
        return new JsonObject
        {
            ["result"] = message,
            ["outcome"] = outcome,
        }.ToJsonString();
    }

    public static ToolApprovalResponseInfo ApplyStoredResult(RunWaitState? wait, ToolApprovalResponseInfo item)
    {
        if (wait is null
            || !RunClockEnvelope.TryReadPackedToolResults(wait.BehaviorJson, out IReadOnlyDictionary<string, PackedToolResult> results)
            || !results.TryGetValue(item.RequestId, out PackedToolResult stored))
        {
            return item;
        }

        return item with
        {
            ResultJson = stored.ResultJson,
            ToolLifecycleAlreadyJournaled = stored.JournaledStart,
        };
    }

    public static ToolApprovalResponseInfo AttachPackResponses(
        IGatewayApprovalStore store,
        RunWaitState? wait,
        ToolApprovalResponseInfo triggering)
    {
        ToolApprovalResponseInfo withStored = ApplyStoredResult(wait, triggering);
        IReadOnlyList<string> packedIds = ReadPackedRequestIds(wait, withStored.RequestId);
        if (packedIds.Count <= 1)
        {
            return withStored;
        }

        IReadOnlyList<ToolApprovalResponseInfo> pack = ToPackResponses(store, packedIds, withStored);
        List<ToolApprovalResponseInfo> siblings = [];
        foreach (ToolApprovalResponseInfo item in pack)
        {
            ToolApprovalResponseInfo merged = ApplyStoredResult(wait, item);
            if (!string.Equals(merged.RequestId, withStored.RequestId, StringComparison.Ordinal))
            {
                siblings.Add(merged);
            }
            else
            {
                withStored = merged;
            }
        }

        if (siblings.Count == 0)
        {
            return withStored;
        }

        return withStored with { PackedSiblingResponses = siblings };
    }

    public static IReadOnlyList<ToolApprovalResponseInfo> ToPackResponses(
        IGatewayApprovalStore store,
        IReadOnlyList<string> packedRequestIds,
        ToolApprovalResponseInfo triggering)
    {
        var responses = new List<ToolApprovalResponseInfo>(packedRequestIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string requestId in packedRequestIds)
        {
            if (!seen.Add(requestId))
            {
                continue;
            }

            if (string.Equals(requestId, triggering.RequestId, StringComparison.Ordinal))
            {
                responses.Add(triggering);
                continue;
            }

            GatewayToolApprovalRequest? row = store.GetById(requestId);
            if (row is null)
            {
                continue;
            }

            bool approved = string.Equals(row.Decision, "approved", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.Decision, "auto_approved", StringComparison.OrdinalIgnoreCase);
            responses.Add(new ToolApprovalResponseInfo(
                row.Id,
                row.ToolCallId,
                row.ToolName,
                row.ArgumentsJson,
                approved,
                row.Reason,
                RunId: row.RunId,
                ContinuationRunId: triggering.ContinuationRunId,
                Expired: string.Equals(row.Decision, "expired", StringComparison.OrdinalIgnoreCase)));
        }

        if (seen.Add(triggering.RequestId))
        {
            responses.Insert(0, triggering);
        }

        return responses;
    }

    /// <summary>
    /// Packed HITL journals one interrupt. Sibling store decisions must not emit
    /// <c>interrupt.resolved</c> until the last human leftover is decided.
    /// </summary>
    public static bool ShouldEmitInterruptResolved(
        IRunWaitStateStore? waitStore,
        IGatewayApprovalStore approvalStore,
        string requestId,
        string? continuationRunId,
        out string interruptId,
        out string waitId)
    {
        interruptId = requestId;
        waitId = NaradEventBodies.ApprovalWaitId(requestId);
        if (waitStore is null)
        {
            return true;
        }

        RunWaitState? wait = FindActiveWait(waitStore, requestId, continuationRunId);
        IReadOnlyList<string> packedIds = ReadPackedRequestIds(wait, requestId);
        if (packedIds.Count <= 1)
        {
            return true;
        }

        if (HasUndecidedHumanSiblings(approvalStore, packedIds, requestId))
        {
            return false;
        }

        interruptId = ResolveJournalInterruptId(wait, requestId);
        waitId = NaradEventBodies.ApprovalWaitId(interruptId);
        return true;
    }

    /// <summary>
    /// Packed HITL journals one <c>interrupt.resolved</c>. The decision must describe the
    /// whole pack, not the leftover that happened to emit the event.
    /// </summary>
    public static string ResolvePackedInterruptStorageDecision(
        IGatewayApprovalStore store,
        IReadOnlyList<string> packedRequestIds,
        string currentRequestId,
        bool currentApproved,
        bool currentExpired,
        bool currentStopRun)
    {
        if (packedRequestIds.Count <= 1)
        {
            return NormalizeStorageDecision(CurrentMemberStorageDecision(
                currentApproved,
                currentExpired,
                currentStopRun));
        }

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string requestId in packedRequestIds)
        {
            if (string.Equals(requestId, currentRequestId, StringComparison.Ordinal))
            {
                normalized.Add(NormalizeStorageDecision(CurrentMemberStorageDecision(
                    currentApproved,
                    currentExpired,
                    currentStopRun)));
                continue;
            }

            GatewayToolApprovalRequest? row = store.GetById(requestId);
            if (row is null || string.IsNullOrWhiteSpace(row.Decision))
            {
                continue;
            }

            normalized.Add(NormalizeStorageDecision(row.Decision));
        }

        return FoldPackedInterruptDecision(normalized);
    }

    /// <summary>
    /// One interrupt.resolved decision for the pack. Any stop wins; else all approve;
    /// else all expired; otherwise deny — mixed leftover is not a protocol fact.
    /// </summary>
    public static string FoldPackedInterruptDecision(IReadOnlyCollection<string> normalized)
    {
        if (normalized.Count == 0)
        {
            return "denied";
        }

        if (normalized.Contains("stopped", StringComparer.OrdinalIgnoreCase))
        {
            return "stopped";
        }

        if (normalized.All(static decision => string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase)))
        {
            return "approved";
        }

        if (normalized.All(static decision => string.Equals(decision, "expired", StringComparison.OrdinalIgnoreCase)))
        {
            return "expired";
        }

        return "denied";
    }

    /// <summary>
    /// Journal <c>interrupt.resolved.decision</c> uses the request <c>actions</c> vocabulary
    /// (<c>approve|deny|stop_run</c>), plus <c>expired</c>. SQLite rows stay storage-shaped.
    /// </summary>
    public static string ToProtocolDecision(string? storageDecision)
        => NormalizeStorageDecision(storageDecision) switch
        {
            "approved" => ApprovalActionCatalog.Approve,
            "stopped" => ApprovalActionCatalog.StopRun,
            "expired" => ApprovalActionCatalog.Expired,
            _ => ApprovalActionCatalog.Deny,
        };

    public static JsonArray? ToInterruptResolvedMembers(
        IGatewayApprovalStore store,
        IReadOnlyList<string> packedRequestIds,
        string? overlayRequestId = null,
        string? overlayStorageDecision = null,
        bool overlayRemainingPendingAsStopped = false)
    {
        if (packedRequestIds.Count <= 1)
        {
            return null;
        }

        JsonNode?[] members =
        [
            .. packedRequestIds.Select(requestId =>
            {
                GatewayToolApprovalRequest? row = store.GetById(requestId);
                if (row is null)
                {
                    return null;
                }

                string? stored = row.Decision;
                if (string.Equals(requestId, overlayRequestId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(overlayStorageDecision))
                {
                    stored = overlayStorageDecision;
                }
                else if (string.IsNullOrWhiteSpace(stored) && overlayRemainingPendingAsStopped)
                {
                    stored = "stopped";
                }

                return (JsonNode?)new JsonObject
                {
                    ["requestId"] = row.Id,
                    ["toolCallId"] = row.ToolCallId,
                    ["toolName"] = row.ToolName,
                    ["toolType"] = row.ToolType,
                    ["decision"] = ToProtocolDecision(stored),
                };
            }),
        ];
        JsonNode?[] present = [.. members.Where(static member => member is not null)];
        return present.Length == 0 ? null : new JsonArray(present);
    }

    /// <summary>
    /// After the current member row is written, remaining NULL decisions mean the pack
    /// is incomplete and must not emit interrupt.resolved or enqueue continuation.
    /// </summary>
    public static bool PackHasPendingMembers(
        IReadOnlyList<string>? packedRequestIds,
        Func<string, string?> readDecision)
    {
        if (packedRequestIds is null || packedRequestIds.Count <= 1)
        {
            return false;
        }

        foreach (string requestId in packedRequestIds)
        {
            if (string.IsNullOrWhiteSpace(readDecision(requestId)))
            {
                return true;
            }
        }

        return false;
    }

    public static string NormalizeStorageDecision(string? decision)
    {
        if (string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "auto_approved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase))
        {
            return "approved";
        }

        if (string.Equals(decision, "expired", StringComparison.OrdinalIgnoreCase))
        {
            return "expired";
        }

        if (string.Equals(decision, "stopped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "stop_run", StringComparison.OrdinalIgnoreCase))
        {
            return "stopped";
        }

        return "denied";
    }

    private static string CurrentMemberStorageDecision(bool approved, bool expired, bool stopRun)
        => expired
            ? "expired"
            : stopRun
                ? "stopped"
                : approved
                    ? "approved"
                    : "denied";

    /// <summary>
    /// Packed HITL post-decide invokes. 0 = unlimited overlap, 1 = serial, N = in-flight cap.
    /// Applies even when MAF <c>AllowConcurrentInvocation</c> is off.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed",
        Justification = "InvokeGatedAsync tasks are started only to be awaited by Task.WhenAll before the semaphore is disposed.")]
    public static async Task<T[]> InvokeThrottledAsync<T>(
        IReadOnlyList<Func<CancellationToken, Task<T>>> workItems,
        int maxParallelTools,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        if (workItems.Count == 0)
        {
            return [];
        }

        int cap = RuntimeSettingKeys.ClampMaxParallelTools(maxParallelTools);
        if (cap == 1)
        {
            var serial = new T[workItems.Count];
            for (int index = 0; index < workItems.Count; index++)
            {
                serial[index] = await workItems[index](cancellationToken).ConfigureAwait(false);
            }

            return serial;
        }

        if (cap == 0)
        {
            return await Task.WhenAll(workItems.Select(item => item(cancellationToken))).ConfigureAwait(false);
        }

        using var gate = new SemaphoreSlim(cap, cap);
        var tasks = new Task<T>[workItems.Count];
        for (int index = 0; index < workItems.Count; index++)
        {
            Func<CancellationToken, Task<T>> item = workItems[index];
            tasks[index] = InvokeGatedAsync(gate, item, cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<T> InvokeGatedAsync<T>(
        SemaphoreSlim gate,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
