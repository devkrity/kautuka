using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace Kautuka.Installer.Windows.Api;

internal static class InstallerEndpoints
{
    internal const string ActionTokenHeaderName = "X-Kautuka-Installer-Token";
    internal static readonly string ActionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static void MapAll(WebApplication app, WindowsApplianceLaunchOptions launchOptions)
    {
        app.MapGet("/api/appliance/bootstrap", () =>
            Results.Ok(new WindowsApplianceBootstrapResponse
            {
                Status = WindowsApplianceInstaller.GetStatus(),
                InstallDefaults = launchOptions.InstallDefaults,
                UninstallDefaults = launchOptions.UninstallDefaults,
                // The token is not published here. Any local account can call this
                // loopback route, and the installer process is elevated.
                ActionToken = string.Empty,
            }));

        app.MapGet("/api/appliance/status", () =>
            Results.Ok(WindowsApplianceInstaller.GetStatus()));

        app.MapGet("/api/appliance/pick-folder", async (HttpContext ctx) =>
        {
            if (!HasActionToken(ctx))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            try
            {
                var initialPath = ctx.Request.Query["initialPath"].ToString();
                var selectedPath = await WindowsApplianceInstaller
                    .PickFolderForInstallAsync(initialPath, ctx.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Ok(new { path = selectedPath });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem($"Folder picker failed: {ex.Message}");
            }
        });

        app.MapPost("/api/appliance/run-install", RunWindowsApplianceInstallSse);

        app.MapPost("/api/appliance/uninstall", async (HttpContext ctx) =>
        {
            if (!HasActionToken(ctx))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var req = await ctx.Request.ReadFromJsonAsync(
                InstallerJsonContext.Default.WindowsApplianceUninstallRequest).ConfigureAwait(false)
                ?? new WindowsApplianceUninstallRequest();

            try
            {
                await WindowsApplianceInstaller.UninstallAsync(req, ctx.RequestAborted).ConfigureAwait(false);
                return Results.Ok(new { message = "Appliance uninstall complete." });
            }
            catch (IOException ex)
            {
                return Results.Problem($"Uninstall failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem($"Uninstall failed: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem($"Uninstall failed: {ex.Message}");
            }
        });

        app.MapPost("/api/exit", (HttpContext ctx, IHostApplicationLifetime lifetime) =>
        {
            if (!HasActionToken(ctx))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(150).ConfigureAwait(false);
                lifetime.StopApplication();
                // Hard-exit fallback in case graceful shutdown stalls.
                await Task.Delay(3000).ConfigureAwait(false);
                Environment.Exit(0);
            });

            return Results.Ok();
        });

        app.MapGet("/api/health", () => Results.Ok("OK"));
    }

    internal static void AddActionToken(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.TryAddWithoutValidation(ActionTokenHeaderName, ActionToken);
    }

    private static bool HasActionToken(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(ActionTokenHeaderName, out StringValues supplied) || supplied.Count != 1)
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(ActionToken);
        byte[] actual = Encoding.UTF8.GetBytes(supplied.ToString());
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static async Task RunWindowsApplianceInstallSse(HttpContext ctx)
    {
        if (!HasActionToken(ctx))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Headers must be set before any async I/O that can throw so that the
        // response is always 200 text/event-stream regardless of what happens next.
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";

        async Task Log(string msg)
        {
            var data = $"data: {JsonSerializer.Serialize(msg, InstallerJsonContext.Default.String)}\n\n";
            await ctx.Response.WriteAsync(data, ctx.RequestAborted).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
        }

        try
        {
            var req = await ctx.Request.ReadFromJsonAsync(
                InstallerJsonContext.Default.WindowsApplianceInstallRequest).ConfigureAwait(false)
                ?? new WindowsApplianceInstallRequest { ReleaseId = null };

            await WindowsApplianceInstaller.InstallAsync(req, Log, ctx.RequestAborted).ConfigureAwait(false);
            await Log("__DONE__").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // intentional: any error must surface as SSE data, never a non-200 HTTP response
        catch (Exception ex)
#pragma warning restore CA1031
        {
            try
            {
                var errLine = $"data: {JsonSerializer.Serialize($"ERROR: {ex.Message}", InstallerJsonContext.Default.String)}\n\n";
                await ctx.Response.WriteAsync(errLine).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
