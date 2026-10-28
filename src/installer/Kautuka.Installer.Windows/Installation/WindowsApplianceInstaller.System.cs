using System.Diagnostics;
using System.Text.Json;

namespace Kautuka.Installer.Windows.Installation;

internal static partial class WindowsApplianceInstaller
{
    private static Dictionary<string, string> GetWslFeatureStates()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft-Windows-Subsystem-Linux"] = GetWindowsFeatureState("Microsoft-Windows-Subsystem-Linux"),
            ["VirtualMachinePlatform"] = GetWindowsFeatureState("VirtualMachinePlatform"),
        };
    }

    private static bool AreWslFeaturesEnabled(IReadOnlyDictionary<string, string> featureStates)
        => string.Equals(featureStates.GetValueOrDefault("Microsoft-Windows-Subsystem-Linux"), "Enabled", StringComparison.OrdinalIgnoreCase)
            && string.Equals(featureStates.GetValueOrDefault("VirtualMachinePlatform"), "Enabled", StringComparison.OrdinalIgnoreCase);

    private static string GetWindowsFeatureState(string featureName)
    {
        var result = RunProcessAsync(
            "dism.exe",
            $"/Online /Get-FeatureInfo /FeatureName:{featureName}",
            allowFailure: true,
            CancellationToken.None).GetAwaiter().GetResult();

        if (result.ExitCode != 0)
            return "Unknown";

        foreach (var line in result.StandardOutput.Split(newLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("State :", StringComparison.OrdinalIgnoreCase))
                continue;

            return line[(line.IndexOf(':') + 1)..].Trim();
        }

        return "Unknown";
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool CommandAvailable(string command)
    {
        var result = RunProcessAsync(
            "where.exe",
            QuoteWindowsArgument(command),
            allowFailure: true,
            CancellationToken.None).GetAwaiter().GetResult();
        return result.ExitCode == 0;
    }

    private static async Task<string?> PickFolderAsync(string? initialPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The appliance installer folder picker is only supported on Windows hosts.");

        if (!CommandAvailable("powershell.exe"))
            throw new InvalidOperationException("powershell.exe is not available on this host.");

        var resolvedInitialPath = string.IsNullOrWhiteSpace(initialPath)
            ? null
            : ResolveWindowsPathWithStableCasing(initialPath);

        var script = $$"""
Add-Type -AssemblyName System.Windows.Forms
$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
$dialog.Description = 'Choose the local Windows folder that Kautuka should manage.'
$dialog.ShowNewFolderButton = $true
if (-not [string]::IsNullOrWhiteSpace({{QuotePowerShellString(resolvedInitialPath ?? string.Empty)}}) -and (Test-Path -LiteralPath {{QuotePowerShellString(resolvedInitialPath ?? string.Empty)}})) {
    $dialog.SelectedPath = {{QuotePowerShellString(resolvedInitialPath ?? string.Empty)}}
}
if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    Write-Output $dialog.SelectedPath
}
""";

        var result = await RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-STA", "-Command", script],
            allowFailure: false,
            cancellationToken).ConfigureAwait(false);

        var selectedPath = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(selectedPath)
            ? null
            : ResolveWindowsPathWithStableCasing(selectedPath);
    }

    private static async Task RunWslBashAsync(
        string distroName,
        string command,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            "wsl.exe",
            ["-d", distroName, "-u", "root", "--", "bash", "-c", command],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 0)
            return;

        if (IsDistroNotFound(result))
        {
            await WaitForDistroReadyAsync(distroName, cancellationToken).ConfigureAwait(false);
            result = await RunProcessAsync(
                "wsl.exe",
                ["-d", distroName, "-u", "root", "--", "bash", "-c", command],
                allowFailure: true,
                cancellationToken).ConfigureAwait(false);
        }

        if (result.ExitCode != 0)
        {
            var detail = string.Join(Environment.NewLine,
                new[] { result.StandardOutput, result.StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)));
            throw new InvalidOperationException(
                $"Command failed (exit {result.ExitCode}): wsl.exe -d {distroName} -u root -- bash -c {command}{Environment.NewLine}{detail}");
        }
    }

    private static async Task WaitForDistroReadyAsync(string distroName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var readinessResult = await RunProcessAsync(
                "wsl.exe",
                ["-d", distroName, "-u", "root", "--exec", "/bin/sh", "-c", "true"],
                allowFailure: true,
                cancellationToken).ConfigureAwait(false);

            if (readinessResult.ExitCode == 0)
                return;

            if (!IsDistroNotFound(readinessResult))
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsDistroNotFound(ProcessResult result)
    {
        var combined = string.Join(Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return combined.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("There is no distribution with the supplied name", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForWebUiReadyAsync(
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(3);
        var pollInterval = TimeSpan.FromSeconds(2);
        var progressLogInterval = TimeSpan.FromSeconds(10);
        var deadlineUtc = DateTime.UtcNow + timeout;
        var nextProgressLogUtc = DateTime.UtcNow + progressLogInterval;
        Exception? lastError = null;

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        await log($"Finalizing appliance startup. Waiting for {webUrl}...").ConfigureAwait(false);

        while (DateTime.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await client.GetAsync(
                    new Uri(webUrl),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    await log($"Verified web UI at {webUrl}.").ConfigureAwait(false);
                    return;
                }

                lastError = new InvalidOperationException(
                    $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"})");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException("The web UI probe timed out.");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }

            if (DateTime.UtcNow >= nextProgressLogUtc)
            {
                await log($"Still finalizing appliance startup. Waiting for {webUrl}...").ConfigureAwait(false);
                nextProgressLogUtc = DateTime.UtcNow + progressLogInterval;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"The appliance services started, but {webUrl} did not become reachable within {timeout.TotalMinutes:0} minutes. Last probe result: {lastError?.Message ?? "unknown"}.");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        bool allowFailure,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (!allowFailure && process.ExitCode != 0)
        {
            var detail = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
            throw new InvalidOperationException(
                $"Command failed (exit {process.ExitCode}): {fileName} {arguments}{Environment.NewLine}{detail}");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool allowFailure,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (!allowFailure && process.ExitCode != 0)
        {
            var detail = string.Join(Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
            throw new InvalidOperationException(
                $"Command failed (exit {process.ExitCode}): {fileName} {string.Join(' ', arguments)}{Environment.NewLine}{detail}");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string QuoteWindowsArgument(string value)
        => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string QuotePowerShellString(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuoteBashArg(string value)
        => $"'{value.Replace("'", "'\"'\"'")}'";

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static class WindowsApplianceStateStore
    {
        private static readonly string installerStateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Kautuka");
        private static readonly string installerStatePath = Path.Combine(installerStateDir, "wsl-installer-state.json");

        public static bool Exists() => File.Exists(installerStatePath);

        public static void Save(WindowsApplianceInstallState state)
        {
            Directory.CreateDirectory(installerStateDir);
            string tempPath = Path.Combine(
                installerStateDir,
                $".{Path.GetFileName(installerStatePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (FileStream stream = new(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(
                        stream,
                        state,
                        InstallerJsonContext.Default.WindowsApplianceInstallState);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, installerStatePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        public static WindowsApplianceInstallState Load()
        {
            if (!File.Exists(installerStatePath))
                throw new InvalidOperationException($"Installer state not found: {installerStatePath}");

            try
            {
                using FileStream stream = new(
                    installerStatePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
                WindowsApplianceInstallState? state = JsonSerializer.Deserialize(
                    stream,
                    InstallerJsonContext.Default.WindowsApplianceInstallState);
                return state ?? throw new InvalidDataException(
                    $"Installer state '{installerStatePath}' contains a JSON null value.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Installer state '{installerStatePath}' contains invalid JSON.", ex);
            }
        }

        public static void Delete()
        {
            if (File.Exists(installerStatePath))
                File.Delete(installerStatePath);
        }
    }
}
