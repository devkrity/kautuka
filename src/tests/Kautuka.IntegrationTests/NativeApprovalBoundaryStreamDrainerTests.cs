using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.IntegrationTests;

public sealed class NativeApprovalBoundaryStreamDrainerTests
{
    [Fact]
    public async Task DrainsNativeIteratorAfterBoundaryWithoutProjectingLaterItems()
    {
        var native = new NativeApprovalPairingSimulator();
        var projected = new List<NativeUpdate>();
        bool boundaryReached = false;

        await foreach (NativeUpdate item in NativeApprovalBoundaryStreamDrainer.DrainAfterBoundaryAsync(
            native.Stream(),
            () => boundaryReached,
            TestContext.Current.CancellationToken))
        {
            projected.Add(item);
            boundaryReached = true;
        }

        Assert.Equal([NativeUpdate.ApprovalBoundary], projected);
        native.BeginContinuation();
    }

    private enum NativeUpdate
    {
        ApprovalBoundary,
        SuppressedCompletion,
    }

    /// <summary>
    /// Models the MAF decorator contract relevant to this boundary: it commits the paired
    /// automatic response only when its stream is drained. Starting a continuation first is
    /// deliberately invalid, matching the framework error we regressed.
    /// </summary>
    private sealed class NativeApprovalPairingSimulator
    {
        private bool pairedResponseCommitted;

        public async IAsyncEnumerable<NativeUpdate> Stream()
        {
            yield return NativeUpdate.ApprovalBoundary;
            await Task.Yield();
            pairedResponseCommitted = true;
            yield return NativeUpdate.SuppressedCompletion;
        }

        public void BeginContinuation()
        {
            if (!pairedResponseCommitted)
                throw new InvalidOperationException("Tool approval request has no matching response.");
        }
    }
}
