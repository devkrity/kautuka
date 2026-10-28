using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class ConcurrencyLimitedAIFunctionTests
{
    [Fact]
    public void ApplySkipsWrapWhenCapIsMissingOrSerial()
    {
        AIFunction tool = CreateTool("alpha");

        Assert.Same(tool, Assert.Single(ConcurrencyLimitedAIFunction.Apply([tool], null)));
        Assert.Same(tool, Assert.Single(ConcurrencyLimitedAIFunction.Apply([tool], 1)));
        Assert.Same(tool, Assert.Single(ConcurrencyLimitedAIFunction.Apply([tool], 0)));
    }

    [Fact]
    public void WrapKeepsApprovalRequiredAsOuterType()
    {
        var gate = new SemaphoreSlim(2, 2);
        AIFunction inner = CreateTool("needs-approval");
        var approval = new ApprovalRequiredAIFunction(inner);

        AIFunction wrapped = ConcurrencyLimitedAIFunction.WrapPreservingApproval(approval, gate);

        ApprovalRequiredAIFunction restored = Assert.IsType<ApprovalRequiredAIFunction>(wrapped);
        ConcurrencyLimitedAIFunction throttled = Assert.IsType<ConcurrencyLimitedAIFunction>(
            ConcurrencyLimitedAIFunction.UnwrapApprovalRequired(restored));
        Assert.Same(inner, throttled.InnerExecutable);
    }

    [Fact]
    public async Task CapLimitsInFlightToolBodies()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int inFlight = 0;
        int peak = 0;

        AIFunction CreateBlocking(string name) => AIFunctionFactory.Create(
            async (CancellationToken cancellationToken) =>
            {
                int current = Interlocked.Increment(ref inFlight);
                int snapshot;
                do
                {
                    snapshot = peak;
                }
                while (current > snapshot && Interlocked.CompareExchange(ref peak, current, snapshot) != snapshot);

                if (Volatile.Read(ref peak) >= 2)
                {
                    started.TrySetResult();
                }

                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
                Interlocked.Decrement(ref inFlight);
                return name;
            },
            name);

        IReadOnlyList<AIFunction> wrapped = ConcurrencyLimitedAIFunction.Apply(
            [CreateBlocking("one"), CreateBlocking("two"), CreateBlocking("three")],
            2);

        Task<object?>[] invocations =
        [
            wrapped[0].InvokeAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask(),
            wrapped[1].InvokeAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask(),
            wrapped[2].InvokeAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask(),
        ];

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, peak);
        Assert.Equal(2, Volatile.Read(ref inFlight));

        release.TrySetResult();
        await Task.WhenAll(invocations).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, peak);
        Assert.Equal(0, Volatile.Read(ref inFlight));
    }

    private static AIFunction CreateTool(string name)
        => AIFunctionFactory.Create(static () => "ok", name);
}
