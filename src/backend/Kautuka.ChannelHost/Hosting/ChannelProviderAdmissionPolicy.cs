namespace Kautuka.ChannelHost.Hosting;

internal static class ChannelProviderAdmissionPolicy
{
    public static ChannelProviderAdmissionDecision Evaluate(string providerKey)
    {
        if (providerKey is not null && (providerKey.Equals("slack", StringComparison.OrdinalIgnoreCase)
            || providerKey.Equals("telegram", StringComparison.OrdinalIgnoreCase)))
            return new(true, "admitted");
        return providerKey is not null && providerKey.Equals("whatsapp", StringComparison.OrdinalIgnoreCase)
            ? new(false, "whatsapp_chg019_deferred_pending_official_polling_compatible_transport")
            : new(false, "provider_not_admitted");
    }
}

internal sealed record ChannelProviderAdmissionDecision(bool Admitted, string Reason);
