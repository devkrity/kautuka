using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.State;

internal sealed class UpdateOperationHistoryStore(IOptions<UpdateOptions> options)
{
    private const int maxRetainedOperations = 10;

    private readonly string historyFilePath = Path.Combine(options.Value.StateRootPath, "update-operations.json");
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask AppendAsync(UpdateOperationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            List<UpdateOperationRecord> existing = await ReadAsync(cancellationToken).ConfigureAwait(false);
            existing.Add(record);

            if (existing.Count > maxRetainedOperations)
            {
                existing.RemoveRange(0, existing.Count - maxRetainedOperations);
            }

            await WriteAsync(existing, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<UpdateOperationRecord>> GetRecentAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<List<UpdateOperationRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(historyFilePath))
        {
            return [];
        }

        List<UpdateOperationRecord>? records = await AtomicJsonStateFile.ReadAsync(
            historyFilePath,
            UpdateStateJsonContext.Default.ListUpdateOperationRecord,
            cancellationToken).ConfigureAwait(false);
        return records ?? throw new InvalidDataException($"State file '{historyFilePath}' contains a JSON null value.");
    }

    private async ValueTask WriteAsync(List<UpdateOperationRecord> records, CancellationToken cancellationToken)
    {
        await AtomicJsonStateFile.WriteAsync(
            historyFilePath,
            records,
            UpdateStateJsonContext.Default.ListUpdateOperationRecord,
            cancellationToken).ConfigureAwait(false);
    }
}
