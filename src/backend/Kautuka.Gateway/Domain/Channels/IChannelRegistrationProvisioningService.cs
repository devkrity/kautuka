namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelRegistrationProvisioningService
{
    Task<ChannelRegistrationProvisioningResult> ProvisionRegistrationAsync(
        string registrationId,
        CancellationToken cancellationToken);

    Task<ChannelRegistrationProvisioningResult> ProvisionAsync(
        string registrationId,
        string channelDeviceId,
        string? defaultTargetType,
        string? defaultTargetId,
        CancellationToken cancellationToken);
}

internal sealed record ChannelRegistrationProvisioningResult(
    bool Success,
    string Status,
    string Detail);
