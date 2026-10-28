namespace Kautuka.Gateway.Domain.Channels;

internal sealed class ChannelSetupRejectedException : Exception
{
    public ChannelSetupRejectedException() : this("channel_setup_rejected", "Channel setup was rejected.") { }

    public ChannelSetupRejectedException(string message) : this("channel_setup_rejected", message) { }

    public ChannelSetupRejectedException(string message, Exception innerException) : base(message, innerException)
        => Code = "channel_setup_rejected";

    public ChannelSetupRejectedException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
