using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Endpoints.Common;
using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Hosting;

internal sealed class GatewayUserRolePrivilegesStartupSyncService(
    IManagerGrpcService managerService,
    IGatewayUserStore users,
    ILogger<GatewayUserRolePrivilegesStartupSyncService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (GatewayUserListItem item in users.ListUsers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!users.TryGetUserById(item.Id, out GatewayUser? user) || user is null)
            {
                continue;
            }

            bool isAdmin = string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);
            NetworkAccessUserPrivilegesMessage existing = await managerService.GetUserPrivilegesAsync(
                new NetworkAccessGetUserPrivilegesRequest
                {
                    Actor = new NetworkAccessActorMessage { UserId = "system", IsAdmin = true },
                    UserId = user.Id,
                    TargetIsAdmin = isAdmin,
                },
                cancellationToken).ConfigureAwait(false);
            if (existing.HasStoredRecord)
            {
                continue;
            }

            await GatewayEndpointUtilities.TrySyncUserRolePrivilegesAsync(
                managerService,
                actorUserId: "system",
                targetUserId: user.Id,
                targetIsAdmin: isAdmin,
                logger,
                cancellationToken,
                failClosed: true).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
