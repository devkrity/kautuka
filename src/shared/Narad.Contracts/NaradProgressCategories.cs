namespace Narad.Contracts;

/// <summary>Namespacing for <c>progress.recorded</c> category values.</summary>
public static class NaradProgressCategories
{
    /// <summary>
    /// One provider generation (one <c>IChatClient</c> request). Independent of tool
    /// execution; packed tool calls share a single turn.
    /// </summary>
    public const string AgentTurn = "agent.turn";
}
