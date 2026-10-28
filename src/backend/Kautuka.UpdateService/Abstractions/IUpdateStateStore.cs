using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.Abstractions;

public interface IUpdateStateStore
{
    ValueTask<UpdateStateRecord> GetAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(UpdateStateRecord state, CancellationToken cancellationToken = default);
}
