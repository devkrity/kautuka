namespace Kautuka.ChannelHost.Hosting;

internal static class ChannelProviderCapabilityCatalog
{
    public static readonly ChannelProviderCapabilities Slack = new(
        ChannelSemanticContractVersions.Capabilities, "slack", true, true, true, false, true, true, true, true, true, true, true, true, false, false,
        40_000, 25, 75, 2_000, 3, 50, 25, 100, 26_214_400);

    public static readonly ChannelProviderCapabilities Telegram = new(
        ChannelSemanticContractVersions.Capabilities, "telegram", true, true, true, true, true, false, false, true, true, false, false, true, true, false,
        32_768, 100, 64, 64, 10, 100, 100, 100, 26_214_400);
}
