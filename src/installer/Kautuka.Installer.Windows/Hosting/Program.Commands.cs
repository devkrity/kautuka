using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Kautuka.Installer.Windows.Hosting;

internal static partial class Program
{
    private static void RunExpressApplianceInstall(ILogger log, WindowsApplianceInstallRequest request)
    {
        ProgramLogMessages.ExpressInstallStarting(log);

        var port = PickPort(log);
        var url = $"http://127.0.0.1:{port}";
        using var ready = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var serverThread = new Thread(() => RunServer(
            url,
            ready,
            log,
            new WindowsApplianceLaunchOptions
            {
                InstallDefaults = request,
                UninstallDefaults = new WindowsApplianceUninstallRequest
                {
                    DistroName = request.DistroName,
                    InstallDir = request.InstallDir,
                    KautukaRoot = request.KautukaRoot,
                },
            },
            cts.Token))
        {
            IsBackground = true,
            Name = "installer-server",
        };
        serverThread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(10)))
        {
            ProgramLogMessages.ServerStartTimeout(log);
            Environment.Exit(1);
        }

        using var http = new HttpClient { BaseAddress = new Uri(url) };

        try
        {
            var route = new Uri("/api/appliance/run-install?mode=express", UriKind.Relative);
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(request, InstallerJsonContext.Default.WindowsApplianceInstallRequest),
                    Encoding.UTF8),
            };
            requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            InstallerEndpoints.AddActionToken(requestMessage);

            using var response = http.Send(requestMessage);
            response.EnsureSuccessStatusCode();

            using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (!string.IsNullOrWhiteSpace(line))
                    ProgramLogMessages.StreamInstallLine(log, line);
            }
        }
        catch (HttpRequestException ex)
        {
            ProgramLogMessages.ExpressInstallFailed(log, ex);
            cts.Cancel();
            Environment.Exit(1);
        }
        catch (IOException ex)
        {
            ProgramLogMessages.ExpressInstallFailed(log, ex);
            cts.Cancel();
            Environment.Exit(1);
        }
        catch (InvalidOperationException ex)
        {
            ProgramLogMessages.ExpressInstallFailed(log, ex);
            cts.Cancel();
            Environment.Exit(1);
        }

        cts.Cancel();
        ProgramLogMessages.ExpressInstallComplete(log);
    }

    /// <summary>
    /// Exit code doubles as the health signal Task Scheduler records in <c>LastTaskResult</c>:
    /// 0 healthy, 1 could not supervise, 2 a host setting is breaking the appliance.
    /// </summary>
    private static void RunApplianceSupervisor(ILogger log, ApplianceSuperviseRequest request)
    {
        ApplianceSupervisorResult result;
        try
        {
            result = ApplianceSupervisor
                .RunAsync(request.DistroName, request.InstallDir, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidOperationException ex)
        {
            ProgramLogMessages.SupervisorFailed(log, ex.Message);
            Environment.Exit(1);
            return;
        }
        catch (IOException ex)
        {
            ProgramLogMessages.SupervisorFailed(log, ex.Message);
            Environment.Exit(1);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            ProgramLogMessages.SupervisorFailed(log, ex.Message);
            Environment.Exit(1);
            return;
        }

        if (result.DistroMissing)
        {
            ProgramLogMessages.SupervisorDistroMissing(log, result.DistroName);
            Environment.Exit(1);
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            ProgramLogMessages.SupervisorFailed(log, result.Error);
            Environment.Exit(1);
            return;
        }

        foreach (var finding in result.Findings)
        {
            if (finding.Severity == WslConfigSeverity.Breaking)
                ProgramLogMessages.SupervisorBreakingFinding(log, finding.Key, finding.Value, finding.Detail);
            else
                ProgramLogMessages.SupervisorDegradingFinding(log, finding.Key, finding.Value, finding.Detail);
        }

        ProgramLogMessages.SupervisorPassComplete(
            log, result.DistroName, result.KeepaliveStarted, result.HostConfigChanged);

        if (result.HasBreakingFinding)
            Environment.Exit(2);
    }

    private static void RunApplianceUninstall(ILogger log, WindowsApplianceUninstallRequest request)
    {
        ProgramLogMessages.UninstallStarting(log);

        var port = PickPort(log);
        var url = $"http://127.0.0.1:{port}";
        using var ready = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var serverThread = new Thread(() => RunServer(
            url,
            ready,
            log,
            new WindowsApplianceLaunchOptions
            {
                InstallDefaults = new WindowsApplianceInstallRequest
                {
                    DistroName = request.DistroName,
                    InstallDir = request.InstallDir ?? DeriveInstallDir(request.DistroName),
                    KautukaRoot = request.KautukaRoot,
                },
                UninstallDefaults = request,
            },
            cts.Token))
        {
            IsBackground = true,
            Name = "installer-server",
        };
        serverThread.Start();
        ready.Wait(TimeSpan.FromSeconds(10));

        using var http = new HttpClient { BaseAddress = new Uri(url) };

        try
        {
            var route = new Uri("/api/appliance/uninstall", UriKind.Relative);
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(request, InstallerJsonContext.Default.WindowsApplianceUninstallRequest),
                    Encoding.UTF8),
            };
            requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            InstallerEndpoints.AddActionToken(requestMessage);

            using var response = http.Send(requestMessage);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            ProgramLogMessages.UninstallRequestFailed(log, ex);
        }
        catch (InvalidOperationException ex)
        {
            ProgramLogMessages.UninstallRequestFailed(log, ex);
        }

        cts.Cancel();
    }
}
