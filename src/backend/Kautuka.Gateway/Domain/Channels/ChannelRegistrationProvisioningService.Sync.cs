namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelRegistrationProvisioningService
{
    public Task<ChannelRegistrationProvisioningResult> ProvisionRegistrationAsync(
        string registrationId,
        CancellationToken cancellationToken)
    {
        return ProvisionRegistrationInternalAsync(registrationId, cancellationToken);
    }

    public Task<ChannelRegistrationProvisioningResult> ProvisionAsync(
        string registrationId,
        string channelDeviceId,
        string? defaultTargetType,
        string? defaultTargetId,
        CancellationToken cancellationToken)
    {
        _ = channelDeviceId;
        _ = defaultTargetType;
        _ = defaultTargetId;
        return ProvisionRegistrationInternalAsync(registrationId, cancellationToken);
    }

    private async Task<ChannelRegistrationProvisioningResult> ProvisionRegistrationInternalAsync(
        string registrationId,
        CancellationToken cancellationToken)
    {
        return await ProvisionRegistrationCoreAsync(registrationId, cancellationToken).ConfigureAwait(false);
    }
}
