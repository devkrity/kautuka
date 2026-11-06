namespace Kautuka.Gateway.Domain.Connectors;

/// <summary>
/// Metadata-only audit events use the existing Gateway console/OTLP observability sinks.
/// Do not pass URLs, header values, credential values, provider bodies or exception objects.
/// Credential-store mutation integrity records remain owned by CredentialsHost.
/// </summary>
internal static partial class RemoteMcpAudit
{
    public static ILogger For(HttpContext context)
        => context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Kautuka.Gateway.RemoteMcpAudit");

    public static void Write(HttpContext context, string operation, string actorId, string connectorId,
        string scope, string outcome, string? credentialKey = null)
    {
        ILogger logger = For(context);
        if (logger.IsEnabled(LogLevel.Information))
            Record(logger, operation, actorId, connectorId, scope, outcome, credentialKey);
    }

    [LoggerMessage(EventId = 7340, Level = LogLevel.Information,
        Message = "Remote MCP audit: operation={Operation} actor={ActorId} connector={ConnectorId} scope={Scope} outcome={Outcome} credential={CredentialKey}")]
    public static partial void Record(ILogger logger, string operation, string actorId, string connectorId,
        string scope, string outcome, string? credentialKey = null);
}
