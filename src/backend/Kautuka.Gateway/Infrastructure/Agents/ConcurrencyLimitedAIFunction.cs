using System.Runtime.CompilerServices;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

/// <summary>
/// Caps how many tool bodies run at once for a single agent runtime.
/// Approval-required tools keep <see cref="ApprovalRequiredAIFunction"/> as the outer
/// type so HITL waits never occupy a concurrency slot.
/// </summary>
internal sealed class ConcurrencyLimitedAIFunction : DelegatingAIFunction
{
    private readonly SemaphoreSlim gate;

    internal ConcurrencyLimitedAIFunction(AIFunction innerFunction, SemaphoreSlim gate)
        : base(innerFunction)
    {
        ArgumentNullException.ThrowIfNull(gate);
        this.gate = gate;
    }

    internal static IReadOnlyList<AIFunction> Apply(IReadOnlyList<AIFunction> tools, int? maxParallel)
    {
        if (tools.Count == 0 || maxParallel is not > 1)
        {
            return tools;
        }

        SemaphoreSlim? gate = new(maxParallel.Value, maxParallel.Value);
        try
        {
            var wrapped = new AIFunction[tools.Count];
            for (int i = 0; i < tools.Count; i++)
            {
                wrapped[i] = WrapPreservingApproval(tools[i], gate);
            }

            gate = null;
            return wrapped;
        }
        finally
        {
            gate?.Dispose();
        }
    }

    internal static AIFunction WrapPreservingApproval(AIFunction tool, SemaphoreSlim gate)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(gate);

        if (tool is ApprovalRequiredAIFunction approval)
        {
            AIFunction executable = UnwrapApprovalRequired(approval);
            return new ApprovalRequiredAIFunction(new ConcurrencyLimitedAIFunction(executable, gate));
        }

        return new ConcurrencyLimitedAIFunction(tool, gate);
    }

    internal static AIFunction UnwrapApprovalRequired(AIFunction tool)
    {
        AIFunction current = tool;
        while (current is ApprovalRequiredAIFunction approval)
        {
            current = GetInnerFunction(approval);
        }

        return current;
    }

    internal AIFunction InnerExecutable => InnerFunction;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_InnerFunction")]
    private static extern AIFunction GetInnerFunction(DelegatingAIFunction function);

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
