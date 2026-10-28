using Microsoft.Extensions.Logging;

namespace Kautuka.Telemetry.Telemetry;

public sealed class KautukaLogLevelSwitch
{
    private volatile int level;

    public KautukaLogLevelSwitch(LogLevel initialLevel)
        => level = (int)initialLevel;

    public LogLevel MinimumLevel => (LogLevel)level;

    public void SetLevel(LogLevel newLevel)
        => level = (int)newLevel;

    public static LogLevel Parse(string? value, LogLevel fallback = LogLevel.Information) => value?.ToUpperInvariant() switch
    {
        "TRACE" => LogLevel.Trace,
        "DEBUG" => LogLevel.Debug,
        "INFO" or "INFORMATION" => LogLevel.Information,
        "WARN" or "WARNING" => LogLevel.Warning,
        "ERROR" => LogLevel.Error,
        "CRITICAL" or "FATAL" => LogLevel.Critical,
        _ => fallback,
    };
}
