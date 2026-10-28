using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Sandbox;

/// <summary>
/// Builds <see cref="AIFunction"/> instances that let the model invoke
/// the sandbox shell for an agent scope.
/// </summary>
internal static class SandboxToolBuilder
{
    /// <summary>
    /// Returns the <c>run_shell</c> tool for an agent sandbox.
    /// The tool executes an arbitrary shell command inside the persistent container
    /// and returns a structured result the model can reason about.
    /// </summary>
    public static AIFunction BuildAgentRunShell(IWorkspaceRuntimeTool workspaceTool, string agentId, string? userId)
    {
        return AIFunctionFactory.Create(
            async (
                [System.ComponentModel.Description("The shell command to execute inside the sandbox container.")] string command,
                CancellationToken cancellationToken) =>
            {
                var result = await workspaceTool.RunAgentShellAsync(agentId, command, userId, cancellationToken: cancellationToken).ConfigureAwait(false);
                return FormatResult(result.ExitCode, result.StdOut, result.StdErr);
            },
            "run_shell",
            "Execute a shell command in the agent's persistent sandbox container. " +
            "Use this to run code, inspect files, install packages, or perform any shell task. " +
            "Returns exit_code, stdout, and stderr.",
            serializerOptions: null);
    }

    private static JsonObject FormatResult(int exitCode, string stdOut, string stdErr) => new()
    {
        ["exit_code"] = exitCode,
        ["stdout"] = stdOut.Length > 8000 ? stdOut[^8000..] : stdOut,
        ["stderr"] = stdErr.Length > 2000 ? stdErr[^2000..] : stdErr,
    };
}
