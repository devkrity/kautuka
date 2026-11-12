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
        EnsureAutoRedirectDisabled(InnerHandler);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAutoRedirectDisabled(InnerHandler);

        Uri? uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        NetworkAccessScope? ambient = NetworkAccessContext.Current;
        if (exemptPredicate?.Invoke(uri) != true)
        {
            await EnforcePolicyAsync(uri, ambient, cancellationToken).ConfigureAwait(false);
        }

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (IsRedirectStatusCode(response.StatusCode) && response.Headers.Location is { } location)
        {
            Uri redirectUri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (redirectUri.IsAbsoluteUri && exemptPredicate?.Invoke(redirectUri) != true)
            {
                try
                {
                    await EnforcePolicyAsync(redirectUri, ambient, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
        }

        return response;
    }

    private async Task EnforcePolicyAsync(Uri uri, NetworkAccessScope? ambient, CancellationToken cancellationToken)
    {
        OutboundTarget target = BuildTarget(uri, ambient, defaultExecutionContext);
        NetworkAccessCheckResult result = await checker.CheckAsync(target, cancellationToken).ConfigureAwait(false);
        if (!result.Allowed)
        {
            LogNetworkAccessDenied(logger, target.Host, target.Port, result.Reason, result.RequestId);
            result.ThrowIfDenied(target);
        }
    }

    private static OutboundTarget BuildTarget(Uri uri, NetworkAccessScope? ambient, string defaultExecutionContext)
    {
        string host = uri.IdnHost;
        int port = uri.Port > 0
            ? uri.Port
            : (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? 443 : 80);

        return new OutboundTarget(
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
    }

    private static bool IsRedirectStatusCode(System.Net.HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code is 300 or 301 or 302 or 303 or 307 or 308;
    }

    private static void EnsureAutoRedirectDisabled(HttpMessageHandler? handler)
    {
        HttpMessageHandler? cursor = handler;
        while (cursor is not null)
        {
            if (cursor is SocketsHttpHandler { AllowAutoRedirect: true } sockets)
            {
                try
                {
                    sockets.AllowAutoRedirect = false;
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(
                        "NetworkAccessCheckHandler requires AllowAutoRedirect = false on inner SocketsHttpHandler.",
                        ex);
                }
            }
            else if (cursor is HttpClientHandler { AllowAutoRedirect: true } httpClientHandler)
            {
                try
                {
                    httpClientHandler.AllowAutoRedirect = false;
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(
                        "NetworkAccessCheckHandler requires AllowAutoRedirect = false on inner HttpClientHandler.",
                        ex);
                }
            }

            cursor = (cursor as DelegatingHandler)?.InnerHandler;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Network access denied for outbound request to {Host}:{Port} (Reason: {Reason}, RequestId: {RequestId})")]
    private static partial void LogNetworkAccessDenied(ILogger logger, string host, int port, string reason, string? requestId);
}
