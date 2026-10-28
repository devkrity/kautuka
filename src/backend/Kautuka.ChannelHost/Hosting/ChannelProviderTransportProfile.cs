namespace Kautuka.ChannelHost.Hosting;

/// <summary>Controls where the normal provider clients send their protocol traffic.</summary>
internal sealed class ChannelProviderTransportProfile
{
    private static readonly Uri officialTelegram = new("https://api.telegram.org/");
    private static readonly Uri officialSlack = new("https://slack.com/");

    public ChannelProviderTransportProfile(IConfiguration configuration, IHostEnvironment environment)
    {
        string mode = configuration["Kautuka:Channels:TransportProfile"] ?? "Official";
        if (!Enum.TryParse(mode, true, out ChannelProviderTransportMode parsed))
            throw new InvalidOperationException("channel_transport_profile_invalid");

        Mode = parsed;
        if (Mode == ChannelProviderTransportMode.Simulator)
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Test"))
                throw new InvalidOperationException("channel_simulator_transport_not_admitted");
            string? baseAddress = configuration["Kautuka:Channels:SimulatorBaseAddress"];
            if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out Uri? configured)
                || !configured.IsLoopback || configured.Scheme != Uri.UriSchemeHttp)
                throw new InvalidOperationException("channel_simulator_loopback_address_required");
            SimulatorBaseAddress = configured.AbsoluteUri.EndsWith('/') ? configured : new Uri(configured.AbsoluteUri + "/");
        }
    }

    public ChannelProviderTransportMode Mode { get; }
    public Uri? SimulatorBaseAddress { get; }
    public bool IsSimulatorSlackSocketUri(Uri uri)
        => Mode == ChannelProviderTransportMode.Simulator
            && uri.IsAbsoluteUri
            && string.Equals(uri.Scheme, "ws", StringComparison.Ordinal)
            && string.Equals(uri.Host, SimulatorBaseAddress!.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == SimulatorBaseAddress.Port
            && string.Equals(uri.AbsolutePath, new Uri(SimulatorBaseAddress, "slack/socket").AbsolutePath, StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);

    public string SanitizedEndpointIdentity => Mode == ChannelProviderTransportMode.Official
        ? "official:pinned"
        : $"simulator:{SimulatorBaseAddress!.Scheme}://{SimulatorBaseAddress.Host}:{SimulatorBaseAddress.Port}";

    public Uri Rewrite(Uri uri)
    {
        if (Mode != ChannelProviderTransportMode.Simulator) return uri;
        // Simulator runs are a network isolation boundary.  A newly introduced
        // provider URL must fail visibly here instead of quietly reaching the
        // public internet (notably upload and CDN URLs returned by providers).
        if (uri.IsLoopback) return uri;
        string prefix = uri.Host.Equals(officialTelegram.Host, StringComparison.OrdinalIgnoreCase) ? "telegram"
            : uri.Host.Equals(officialSlack.Host, StringComparison.OrdinalIgnoreCase) ? "slack"
            : string.Empty;
        if (string.IsNullOrEmpty(prefix))
            throw new InvalidOperationException("channel_simulator_external_transport_denied");
        return new Uri(SimulatorBaseAddress!, prefix + uri.PathAndQuery);
    }
}

internal enum ChannelProviderTransportMode { Official, Simulator }

internal sealed class ChannelProviderTransportRewriteHandler(ChannelProviderTransportProfile profile) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { IsAbsoluteUri: true } uri)
            request.RequestUri = profile.Rewrite(uri);
        return base.SendAsync(request, cancellationToken);
    }
}

internal static class ChannelProviderTransportUriExtensions
{
    public static Uri ToWebSocketUri(this Uri uri)
    {
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" };
        return builder.Uri;
    }
}
