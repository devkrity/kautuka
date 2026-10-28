using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kautuka.Installer.Windows.Hosting;

internal static partial class Program
{
    private static void RunServer(string url, ManualResetEventSlim serverReady,
        ILogger log, WindowsApplianceLaunchOptions launchOptions, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.AddKautukaLogging(LogLevel.Warning, "kautuka-installer-windows");

        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.TypeInfoResolverChain.Insert(0, InstallerJsonContext.Default);
        });

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            string host = context.Request.Host.Host;
            if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        var wwwroot = app.Environment.WebRootPath
            ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot),
            });
        }

        InstallerEndpoints.MapAll(app, launchOptions);

        var embeddedIndex = TryLoadEmbeddedIndexHtml();
        if (!string.IsNullOrWhiteSpace(embeddedIndex))
        {
            app.MapGet("/", async ctx =>
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(embeddedIndex, ctx.RequestAborted).ConfigureAwait(false);
            });

            app.MapFallback(async ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(embeddedIndex, ctx.RequestAborted).ConfigureAwait(false);
            });
        }
        else
        {
            ProgramLogMessages.WwwRootNotFound(log, "embedded resource 'Kautuka.Installer.Windows.wwwroot.index.html'");
            app.MapGet("/", () => Results.Text(
                "Installer UI is missing. Embedded index resource not found.",
                "text/plain"));
        }

        var lifetime = app.Services.GetService(typeof(IHostApplicationLifetime)) as IHostApplicationLifetime;
        lifetime?.ApplicationStarted.Register(() =>
        {
            serverReady.Set();
            Console.WriteLine($"READY:{url}");
        });

        ct.Register(() => lifetime?.StopApplication());
        app.Run();
    }

    private static ushort PickPort(ILogger log)
    {
        var port = IsPortFree(9500) ? (ushort)9500 : FindFreePort(9501);
        ProgramLogMessages.ServerPort(log, port);
        return port;
    }

    private static ushort FindFreePort(ushort start)
    {
        for (var p = start; p < 65000; p++)
            if (IsPortFree(p)) return p;

        return 9999;
    }

    private static bool IsPortFree(ushort port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string? TryLoadEmbeddedIndexHtml()
    {
        var asm = typeof(Program).Assembly;
        using var stream = asm.GetManifestResourceStream("Kautuka.Installer.Windows.wwwroot.index.html");
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
