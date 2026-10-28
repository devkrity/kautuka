namespace Kautuka.Gateway.Domain.Execution;

internal static class ActionIdentity
{
    public static string ResolveKind(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return "system";

        if (toolName.StartsWith("invoke_agent_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "invoke_agent", StringComparison.OrdinalIgnoreCase))
            return "agent";

        if (toolName.StartsWith("run_skill_", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("read_skill_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "load_skill", StringComparison.OrdinalIgnoreCase))
            return "skill";

        if (string.Equals(toolName, "run_shell", StringComparison.OrdinalIgnoreCase))
            return "superexec";

        return "connector";
    }

    public static bool IsAgentToolInvoke(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName)
            && (toolName.StartsWith("invoke_agent_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "invoke_agent", StringComparison.OrdinalIgnoreCase));
}
