namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private string ResolveApprovalActorLabel(string? decidedByUserId)
    {
        if (string.IsNullOrWhiteSpace(decidedByUserId))
        {
            return string.Empty;
        }

        GatewayUser? user = state.Users.Values.FirstOrDefault(item =>
            string.Equals(item.Id, decidedByUserId, StringComparison.Ordinal));

        if (user is null)
        {
            return decidedByUserId;
        }

        return string.IsNullOrWhiteSpace(user.Username)
            ? user.Id
            : user.Username;
    }
}
