using System.Diagnostics.CodeAnalysis;

namespace Kautuka.NetworkAccess.Client;

/// <summary>
/// Exception thrown when an outbound HTTP or WebSocket connection is blocked, paused, or unavailable.
/// </summary>
public sealed class NetworkAccessDeniedException : HttpRequestException
{
    public const string CodeRequired = "network_access_required";
    public const string CodeUnavailable = "network_access_unavailable";
    public const string CodePaused = "network_paused";
    public const string DefaultApprovalUrl = "/network-access";

    public NetworkAccessDenialKind Kind { get; }
    public string Destination { get; }
    public int Port { get; }
    public string Scheme { get; }
    public string? Service { get; }
    public string? Purpose { get; }
    public string? UserId { get; }
    public string? AgentId { get; }
    public string? ConnectorId { get; }
    public string? RequestId { get; }
    public string Reason { get; }
    public string NaradReason { get; }

    [SuppressMessage("Design", "CA1056:UriPropertiesShouldNotBeStrings", Justification = "Matches Narad protocol and specification string field requirement.")]
    public string ApprovalUrl { get; }

    public NetworkAccessDeniedException()
        : base("Network access was denied.")
    {
        Kind = NetworkAccessDenialKind.Denied;
        Destination = string.Empty;
        Port = 0;
        Scheme = "https";
        Reason = "denied";
        NaradReason = CodeRequired;
        ApprovalUrl = DefaultApprovalUrl;
    }

    public NetworkAccessDeniedException(string? message)
        : base(message)
    {
        Kind = NetworkAccessDenialKind.Denied;
        Destination = string.Empty;
        Port = 0;
        Scheme = "https";
        Reason = "denied";
        NaradReason = CodeRequired;
        ApprovalUrl = DefaultApprovalUrl;
    }

    public NetworkAccessDeniedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
        Kind = NetworkAccessDenialKind.Denied;
        Destination = string.Empty;
        Port = 0;
        Scheme = "https";
        Reason = "denied";
        NaradReason = CodeRequired;
        ApprovalUrl = DefaultApprovalUrl;
    }

    public NetworkAccessDeniedException(
        string destination,
        int port,
        string scheme,
        NetworkAccessDenialKind kind,
        string reason,
        string? requestId = null,
        string? userId = null,
        string? agentId = null,
        string? connectorId = null,
        string? service = null,
        string? purpose = null,
        string? customMessage = null,
        Exception? innerException = null)
        : base(FormatMessage(kind, destination, port, userId, customMessage), innerException)
    {
        Destination = destination;
        Port = port;
        Scheme = scheme;
        Kind = kind;
        Reason = reason;
        RequestId = requestId;
        UserId = userId;
        AgentId = agentId;
        ConnectorId = connectorId;
        Service = service;
        Purpose = purpose;
        NaradReason = ResolveNaradReason(kind);
        ApprovalUrl = DefaultApprovalUrl;
    }

    public static string ResolveNaradReason(NetworkAccessDenialKind kind) => kind switch
    {
        NetworkAccessDenialKind.Paused => CodePaused,
        NetworkAccessDenialKind.Unavailable => CodeUnavailable,
        _ => CodeRequired,
    };

    private static string FormatMessage(
        NetworkAccessDenialKind kind,
        string destination,
        int port,
        string? userId,
        string? customMessage)
    {
        if (!string.IsNullOrWhiteSpace(customMessage))
        {
            return customMessage;
        }

        string destFormatted = (port is 0 or 80 or 443) ? destination : $"{destination}:{port}";

        return kind switch
        {
            NetworkAccessDenialKind.Unavailable =>
                "Network access control isn't responding, so this connection was stopped. It isn't a permission problem.",
            NetworkAccessDenialKind.Paused =>
                "Network access has been paused.",
            _ =>
                $"This agent hasn't been allowed to reach {destFormatted} yet. A request has been sent to {(string.IsNullOrWhiteSpace(userId) ? "the owner" : userId)}. Tell the user it needs approval, then try again once it's approved.",
        };
    }
}
