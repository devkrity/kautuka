namespace Kautuka.Gateway.Domain.Sessions;

internal interface ISessionTitleService
{
    string InitialTitle(string? originator = null);

    string ResolveTitle(string currentTitle, string? originator, string targetType, string targetId, IReadOnlyList<GatewayMessage> messages);
}
