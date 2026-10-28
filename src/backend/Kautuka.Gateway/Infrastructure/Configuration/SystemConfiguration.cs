namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class SystemDefinition
{
    public string LogLevel { get; init; } = "DEBUG";

    public string Locale { get; init; } = "en-US";

    public string TimeZone { get; init; } = "UTC";

    public ObservabilityDefinition Observability { get; init; } = new();
}

internal sealed class ObservabilityDefinition
{
    public bool IncludeBinaryContent { get; init; } = true;

    public bool CaptureMessageContent { get; init; }
}
