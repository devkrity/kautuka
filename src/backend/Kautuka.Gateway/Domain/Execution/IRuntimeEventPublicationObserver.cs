using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal interface IRuntimeEventPublicationObserver
{
    ValueTask OnPublishedAsync(NaradPersistedEvent persistedEvent, CancellationToken cancellationToken = default);
}
