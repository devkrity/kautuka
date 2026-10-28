using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.State;

internal sealed class JsonHostedCatalogStateStore(
    IOptions<UpdateOptions> options) : IHostedCatalogStateStore
{
    private readonly string stateFilePath = Path.Combine(options.Value.StateRootPath, "catalog-state.json");
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask<HostedCatalogSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath)!);

            if (!File.Exists(stateFilePath))
            {
                return HostedCatalogSnapshot.CreateDefault();
            }

            HostedCatalogSnapshot? snapshot = await AtomicJsonStateFile.ReadAsync(
                stateFilePath,
                HostedCatalogStateJsonContext.Default.HostedCatalogSnapshot,
                cancellationToken).ConfigureAwait(false);
            return snapshot ?? throw new InvalidDataException($"State file '{stateFilePath}' contains a JSON null value.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SaveAsync(HostedCatalogSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await AtomicJsonStateFile.WriteAsync(
                stateFilePath,
                snapshot,
                HostedCatalogStateJsonContext.Default.HostedCatalogSnapshot,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
