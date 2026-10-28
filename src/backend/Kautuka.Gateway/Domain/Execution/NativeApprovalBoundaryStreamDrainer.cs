using System.Runtime.CompilerServices;

namespace Kautuka.Gateway.Domain.Execution;

/// <summary>
/// Lets the native MAF stream finish after Kautuka has reached an approval boundary.
/// This preserves framework-owned request/response pairing while preventing post-boundary
/// content from being projected into the paused Kautuka run.
/// </summary>
internal static class NativeApprovalBoundaryStreamDrainer
{
    internal static async IAsyncEnumerable<T> DrainAfterBoundaryAsync<T>(
        IAsyncEnumerable<T> source,
        Func<bool> boundaryReached,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (T item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!boundaryReached())
            {
                yield return item;
            }
        }
    }
}
