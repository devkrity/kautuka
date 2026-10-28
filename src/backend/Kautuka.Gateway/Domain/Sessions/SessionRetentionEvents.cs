using Mediator;

namespace Kautuka.Gateway.Domain.Sessions;

internal sealed record SessionRetentionRequested(
    string TargetType,
    string TargetId,
    string OwnerUserId,
    string SessionId,
    int RetainCount) : INotification;

internal sealed record SessionRetentionWork(
    string TargetType,
    string TargetId,
    string OwnerUserId,
    string SessionId,
    int RetainCount);
