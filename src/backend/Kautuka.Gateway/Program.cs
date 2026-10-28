using System.Net;
using System.Reflection;

KautukaAspNetCoreStartupPolicy.SanitizeHostingStartupAssemblies();

var builder = WebApplication.CreateSlimBuilder(args);
var logLevelSwitch = builder.AddKautukaLogging(LogLevel.Debug);
GatewayAppBootstrap.ConfigureProductionServer(builder, IPAddress.Loopback);
GatewayAppBootstrap.ConfigureServices(builder);

var app = builder.Build();
string buildVersion = typeof(GatewayProgramLog).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion
    ?? "unknown";
GatewayProgramLog.StartupHostBuilt(app.Logger, app.Environment.EnvironmentName, app.Environment.ContentRootPath, buildVersion);

// The current schema must be bootstrapped and validated before GatewayState is resolved.
// GatewayState.FromDatabase deliberately rejects an absent or incompatible database.
KautukaDatabase database = app.Services.GetRequiredService<KautukaDatabase>();
GatewayProgramLog.StartupDatabaseResolved(app.Logger);
GatewayAppBootstrap.ConfigureApp(app, database);
GatewayProgramLog.StartupConfigureAppCompleted(app.Logger);

if (app.Environment.IsDevelopment())
{
    logLevelSwitch.SetLevel(LogLevel.Debug);
}
else
{
    // Keep host startup non-blocking; apply persisted log level in the background.
    _ = Task.Run(() =>
    {
        try
        {
            GatewayState gatewayState = app.Services.GetRequiredService<GatewayState>();
            logLevelSwitch.SetLevel(KautukaLogLevelSwitch.Parse(gatewayState.SystemSettings.LogLevel, LogLevel.Debug));
        }
        catch (InvalidOperationException ex)
        {
            GatewayProgramLog.LogLevelLoadFailed(app.Logger, ex);
        }
    });
}

await app.RunAsync().ConfigureAwait(false);

static partial class GatewayProgramLog
{
    [LoggerMessage(EventId = 0, Level = LogLevel.Information, Message = "[GatewayStartup] Host built. Environment={Environment} ContentRoot={ContentRoot} BuildVersion={BuildVersion}")]
    public static partial void StartupHostBuilt(ILogger logger, string environment, string contentRoot, string buildVersion);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "[GatewayStartup] Database resolved. Starting ConfigureApp")]
    public static partial void StartupDatabaseResolved(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "[GatewayStartup] ConfigureApp completed. Starting host run")]
    public static partial void StartupConfigureAppCompleted(ILogger logger);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Gateway host started with effective log level code {LogLevelCode}")]
    public static partial void HostStarted(ILogger logger, int logLevelCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to apply persisted system log level during startup")]
    public static partial void LogLevelLoadFailed(ILogger logger, Exception exception);
}
