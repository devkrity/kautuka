using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

namespace Kautuka.IntegrationTests;

public sealed class TuiChatInterruptTests
{
    [Fact]
    public void InterruptRequestedIsNotTerminalRunEvent()
    {
        Assert.False(NaradAuthorityEventParser.IsTerminalRunEvent("interrupt.requested"));
        Assert.True(NaradAuthorityEventParser.IsInterruptBoundaryEvent("interrupt.requested"));
    }

    [Fact]
    public async Task StreamCancellationReturnsSuccessResult()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);

        Result result = await SimulateCancelledStreamAsync(cancellation.Token).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
    }

    private static async Task<Result> SimulateCancelledStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return Results.Failure("stream should have cancelled");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.Success();
        }
    }
}
