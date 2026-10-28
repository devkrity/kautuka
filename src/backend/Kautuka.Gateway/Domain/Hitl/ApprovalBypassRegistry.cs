using System.Collections.Concurrent;

namespace Kautuka.Gateway.Domain.Hitl;

/// <summary>
/// In-memory registry of run-scoped approval bypasses.
/// When a user chooses "Approve All for this run", the run ID is registered here.
/// All subsequent tool approval requests for that run are auto-approved without pausing.
/// The bypass is cleared automatically when the run ends.
/// </summary>
internal interface IApprovalBypassRegistry
{
    /// <summary>Registers a bypass for the given run, approved by the specified user.</summary>
    void RegisterBypass(string runId, string userId);

    /// <summary>
    /// Returns true if a bypass is active for <paramref name="runId"/> and outputs the user who activated it.
    /// </summary>
    bool IsActive(string runId, out string userId);

    /// <summary>Removes the bypass for the given run (called when the run ends).</summary>
    void Clear(string runId);
}

internal sealed class ApprovalBypassRegistry : IApprovalBypassRegistry
{
    private readonly ConcurrentDictionary<string, string> active = new(StringComparer.Ordinal);

    public void RegisterBypass(string runId, string userId)
        => active[runId] = userId;

    public bool IsActive(string runId, out string userId)
        => active.TryGetValue(runId, out userId!);

    public void Clear(string runId)
        => active.TryRemove(runId, out _);
}
