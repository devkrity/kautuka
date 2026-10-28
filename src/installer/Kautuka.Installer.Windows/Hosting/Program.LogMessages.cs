using Microsoft.Extensions.Logging;

namespace Kautuka.Installer.Windows.Hosting;

internal static partial class ProgramLogMessages
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Appliance install: starting headless install with resolved defaults.")]
    internal static partial void ExpressInstallStarting(ILogger logger);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
        Message = "Installer server did not start in time. Aborting express install.")]
    internal static partial void ServerStartTimeout(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Express install failed.")]
    internal static partial void ExpressInstallFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error,
        Message = "Uninstall request failed.")]
    internal static partial void UninstallRequestFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "Express install complete.")]
    internal static partial void ExpressInstallComplete(ILogger logger);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information,
        Message = "Appliance uninstall: submitting uninstall request.")]
    internal static partial void UninstallStarting(ILogger logger);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Warning,
        Message = "Installer server did not signal ready - opening window anyway.")]
    internal static partial void ServerReadySignalMissing(ILogger logger);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Information,
        Message = "Opening installer at {Url}")]
    internal static partial void OpeningInstaller(ILogger logger, string url);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning,
        Message = "wwwroot not found at '{Path}' - build the installer UI first.")]
    internal static partial void WwwRootNotFound(ILogger logger, string path);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Debug,
        Message = "Installer server port: {Port}")]
    internal static partial void ServerPort(ILogger logger, ushort port);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information,
        Message = "{Line}")]
    internal static partial void StreamInstallLine(ILogger logger, string line);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning,
        Message = "Failed to launch browser for installer URL '{Url}'.")]
    internal static partial void BrowserLaunchFailed(ILogger logger, Exception exception, string url);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information,
        Message = "Opened installer in embedded webview at {Url}.")]
    internal static partial void EmbeddedWindowLaunched(ILogger logger, string url);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Debug,
        Message = "Embedded webview launch failed; falling back to default browser.")]
    internal static partial void EmbeddedWindowLaunchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Information,
        Message = "Appliance supervisor: distro '{Distro}' is resident. keepaliveRestored={KeepaliveRestored} hostConfigChanged={HostConfigChanged}")]
    internal static partial void SupervisorPassComplete(
        ILogger logger, string distro, bool keepaliveRestored, bool hostConfigChanged);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Warning,
        Message = "Appliance supervisor: WSL distro '{Distro}' is not registered for this user. Nothing to supervise.")]
    internal static partial void SupervisorDistroMissing(ILogger logger, string distro);

    [LoggerMessage(EventId = 1016, Level = LogLevel.Error,
        Message = "Appliance supervisor: {Error}")]
    internal static partial void SupervisorFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 1017, Level = LogLevel.Error,
        Message = "Appliance supervisor: host setting {Key}={Value} breaks the appliance. {Detail}")]
    internal static partial void SupervisorBreakingFinding(
        ILogger logger, string key, string value, string detail);

    [LoggerMessage(EventId = 1018, Level = LogLevel.Warning,
        Message = "Appliance supervisor: host setting {Key}={Value} degrades the appliance. {Detail}")]
    internal static partial void SupervisorDegradingFinding(
        ILogger logger, string key, string value, string detail);
}
