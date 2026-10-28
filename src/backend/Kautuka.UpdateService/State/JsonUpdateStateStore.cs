using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.State;

internal sealed class JsonUpdateStateStore(
    IOptions<UpdateOptions> options) : IUpdateStateStore
{
    private readonly string stateFilePath = Path.Combine(options.Value.StateRootPath, "update-state.json");
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask<UpdateStateRecord> GetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath)!);

            UpdateStateRecord? state = await TryReadAsync(stateFilePath, cancellationToken).ConfigureAwait(false);
            if (state is not null)
            {
                return state;
            }

            if (File.Exists(stateFilePath))
            {
                throw new InvalidDataException($"State file '{stateFilePath}' contains a JSON null value.");
            }

            return UpdateStateRecord.CreateDefault();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask SaveAsync(UpdateStateRecord state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<UpdateStateRecord?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await AtomicJsonStateFile.ReadAsync(
            path,
            UpdateStateJsonContext.Default.UpdateStateRecord,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteStateAsync(UpdateStateRecord state, CancellationToken cancellationToken)
    {
        await WriteStateFileAsync(stateFilePath, state, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteStateFileAsync(string path, UpdateStateRecord state, CancellationToken cancellationToken)
    {
        await AtomicJsonStateFile.WriteAsync(
            path,
            state,
            UpdateStateJsonContext.Default.UpdateStateRecord,
            cancellationToken).ConfigureAwait(false);
    }
}
