using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.NetworkAccess.Client;

/// <summary>
/// DelegatingHandler that intercepts outbound HTTP requests and checks network access policy before delegating.
/// Throws <see cref="NetworkAccessDeniedException"/> when connection is not permitted.
/// </summary>
public sealed partial class NetworkAccessCheckHandler : DelegatingHandler
{
    private readonly INetworkAccessChecker checker;
    private readonly ILogger logger;
    private readonly Func<Uri, bool>? exemptPredicate;
    private readonly string defaultExecutionContext;

    public NetworkAccessCheckHandler(
        INetworkAccessChecker checker,
        ILogger<NetworkAccessCheckHandler>? logger = null,
        Func<Uri, bool>? exemptPredicate = null,
        string defaultExecutionContext = "app")
    {
        this.checker = checker ?? throw new ArgumentNullException(nameof(checker));
        this.logger = logger ?? NullLogger<NetworkAccessCheckHandler>.Instance;
        this.exemptPredicate = exemptPredicate;
        this.defaultExecutionContext = defaultExecutionContext;
    }

    public NetworkAccessCheckHandler(
        INetworkAccessChecker checker,
        HttpMessageHandler innerHandler,
        ILogger<NetworkAccessCheckHandler>? logger = null,
        Func<Uri, bool>? exemptPredicate = null,
        string defaultExecutionContext = "app")
        : base(innerHandler ?? throw new ArgumentNullException(nameof(innerHandler)))
    {
        this.checker = checker ?? throw new ArgumentNullException(nameof(checker));
        this.logger = logger ?? NullLogger<NetworkAccessCheckHandler>.Instance;
        this.exemptPredicate = exemptPredicate;
        this.defaultExecutionContext = defaultExecutionContext;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Uri? uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (exemptPredicate?.Invoke(uri) == true)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        string host = uri.IdnHost;
        int port = uri.Port > 0
            ? uri.Port
            : (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? 443 : 80);

        NetworkAccessScope? ambient = NetworkAccessContext.Current;

        var target = new OutboundTarget(
            Host: host,
            Port: port,
            Scheme: uri.Scheme,
            Service: ambient?.Service ?? "http",
            Purpose: ambient?.Purpose ?? "http-request",
            UserId: ambient?.UserId,
            AgentId: ambient?.AgentId,
            ConnectorId: ambient?.ConnectorId,
            Systemwide: ambient?.Systemwide ?? false,
            ExecutionContext: ambient?.ExecutionContext ?? defaultExecutionContext);

        NetworkAccessCheckResult result = await checker.CheckAsync(target, cancellationToken).ConfigureAwait(false);
        if (!result.Allowed)
        {
            LogNetworkAccessDenied(logger, host, port, result.Reason, result.RequestId);
            result.ThrowIfDenied(target);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Network access denied for outbound request to {Host}:{Port} (Reason: {Reason}, RequestId: {RequestId})")]
    private static partial void LogNetworkAccessDenied(ILogger logger, string host, int port, string reason, string? requestId);
}
