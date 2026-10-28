using System.Threading.Channels;

using Grpc.Core;

using Kautuka.Protos;

namespace Kautuka.Gateway.Infrastructure.Channels;

internal readonly record struct ChannelHostOwnerTargetSyncWork(
    string OwnerUserId,
    string Reason,
    string OperationId);

internal interface IChannelHostOwnerTargetSyncQueue
{
    bool TryEnqueue(ChannelHostOwnerTargetSyncWork work);

    IAsyncEnumerable<ChannelHostOwnerTargetSyncWork> DequeueAllAsync(CancellationToken cancellationToken);
}

internal interface IChannelHostOwnerTargetSyncNotifier
{
    bool TryNotify(string ownerUserId, string reason);
}

internal sealed class ChannelHostOwnerTargetSyncQueue : IChannelHostOwnerTargetSyncQueue
{
    private readonly Channel<ChannelHostOwnerTargetSyncWork> channel = Channel.CreateBounded<ChannelHostOwnerTargetSyncWork>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public bool TryEnqueue(ChannelHostOwnerTargetSyncWork work) => channel.Writer.TryWrite(work);

    public IAsyncEnumerable<ChannelHostOwnerTargetSyncWork> DequeueAllAsync(CancellationToken cancellationToken)
        => channel.Reader.ReadAllAsync(cancellationToken);
}

internal sealed class ChannelHostOwnerTargetSyncNotifier(
    IChannelHostOwnerTargetSyncQueue queue) : IChannelHostOwnerTargetSyncNotifier
{
    public bool TryNotify(string ownerUserId, string reason)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return false;
        }

        return queue.TryEnqueue(new ChannelHostOwnerTargetSyncWork(
            ownerUserId.Trim(), reason.Trim(), Guid.NewGuid().ToString("N")));
    }
}

internal sealed partial class ChannelHostOwnerTargetSyncDispatcherService(
    IChannelHostOwnerTargetSyncQueue queue,
    IChannelHostGrpcService channelHost,
    GatewayState state,
    ILogger<ChannelHostOwnerTargetSyncDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (ChannelHostOwnerTargetSyncWork work in queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {

                using IDisposable scope = logger.BeginKautukaCorrelationScope(
                    ("OwnerUserId", work.OwnerUserId),
                    ("Reason", work.Reason));

                try
                {
                    SyncOwnerTargetsRequest request = BuildRequest(work);
                    SyncOwnerTargetsResponse response = await channelHost.SyncOwnerTargetsAsync(request, stoppingToken).ConfigureAwait(false);
                    LogOwnerTargetSyncCompleted(logger, work.OwnerUserId, response.RegistrationsSynced, response.Detail);
                }
                catch (Exception ex) when (ex is InvalidOperationException or RpcException or OperationCanceledException)
                {
                    if (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    LogOwnerTargetSyncFailed(logger, ex, work.OwnerUserId, work.Reason);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal SyncOwnerTargetsRequest BuildRequest(ChannelHostOwnerTargetSyncWork work)
    {
        SyncOwnerTargetsRequest request = new()
        {
            Context = BuildSystemContext(work.OwnerUserId),
            OwnerUserId = work.OwnerUserId,
            Reason = string.IsNullOrWhiteSpace(work.Reason) ? "owner_target_sync" : work.Reason,
            BotName = "Kautuka",
            IdempotencyKey = work.OperationId,
        };

        GatewayUser? owner = state.Users.Values.FirstOrDefault(user => string.Equals(user.Id, work.OwnerUserId, StringComparison.Ordinal));
        GatewayAgent[] agents = [.. state.Agents.Values
            .Where(agent => GatewayOwnershipAccess.CanAccessAgent(owner, agent))
            .OrderBy(agent => agent.Name, StringComparer.Ordinal)
            .ThenBy(agent => agent.Id, StringComparer.Ordinal)];

        request.Targets.AddRange(agents.Select(static agent => new ChannelTargetMessage
        {
            TargetKey = $"agent:{agent.Id}",
            DisplayName = string.IsNullOrWhiteSpace(agent.Name) ? "Agent: Assistant" : $"Agent: {agent.Name}",
        }));
        request.Commands.AddRange(ChannelBotCommandCatalogue.Default.Select(static command => new ChannelCommandMessage
        {
            Command = command.Command,
            Description = command.Description,
        }));

        return request;
    }

    private static ScopedRequestContext BuildSystemContext(string actorUserId) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        ActorUserId = actorUserId,
        SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        GatewaySessionId = "gateway-owner-target-sync",
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Owner target sync completed. ownerUserId={OwnerUserId} registrationsSynced={RegistrationsSynced} detail={Detail}")]
    private static partial void LogOwnerTargetSyncCompleted(ILogger logger, string ownerUserId, int registrationsSynced, string detail);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Owner target sync failed. ownerUserId={OwnerUserId} reason={Reason}")]
    private static partial void LogOwnerTargetSyncFailed(ILogger logger, Exception exception, string ownerUserId, string reason);
}

internal sealed class ChannelHostOwnerTargetReconciliationService(
    GatewayState state,
    IChannelHostOwnerTargetSyncNotifier notifier) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Reconcile();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            Reconcile();
        }
    }

    internal int Reconcile()
    {
        string[] owners = [.. state.Agents.Values
            .Select(static agent => agent.OwnerUserId)
            .Concat(state.Users.Values.Select(static user => user.Id))
            .Where(static owner => !string.IsNullOrWhiteSpace(owner))
            .Select(static owner => owner!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        return owners.Count(owner => notifier.TryNotify(owner, "periodic_reconciliation"));
    }
}
