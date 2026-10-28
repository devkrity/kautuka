using Kautuka.Manager.Protos;

namespace Kautuka.UpdateService.Hosting;

public interface IManagerStatusClient
{
    Task<ManagerStatusResponse> GetHostStatusAsync(CancellationToken cancellationToken);
}
