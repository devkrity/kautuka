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
        string distroName,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(10);
        var pollInterval = TimeSpan.FromSeconds(2);
        var progressLogInterval = TimeSpan.FromSeconds(10);
        var heartbeatInterval = TimeSpan.FromSeconds(30);
        var deadlineUtc = DateTime.UtcNow + timeout;
        var nextProgressLogUtc = DateTime.UtcNow + progressLogInterval;
        var nextHeartbeatUtc = DateTime.UtcNow + heartbeatInterval;
        var startupUtc = DateTimeOffset.UtcNow;
        string? previousProgress = null;
        Exception? lastError = null;

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        await log($"Finalizing appliance startup. Waiting for Gateway and the setup page at {webUrl}...").ConfigureAwait(false);

        while (DateTime.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var healthResponse = await client.GetAsync(
                    new Uri($"{webUrl}/api/runtime/system-health"),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!healthResponse.IsSuccessStatusCode)
                {
                    lastError = new InvalidOperationException(
                        $"Gateway health endpoint returned HTTP {(int)healthResponse.StatusCode} ({healthResponse.ReasonPhrase ?? "unknown"}).");
                }
                else
                {
                    using Stream healthStream = await healthResponse.Content
                        .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using JsonDocument healthDocument = await JsonDocument.ParseAsync(
                        healthStream,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    JsonElement health = healthDocument.RootElement;
                    bool coreReady = health.TryGetProperty("lifecycle", out JsonElement lifecycle)
                        && lifecycle.TryGetProperty("coreReady", out JsonElement coreReadyValue)
                        && coreReadyValue.ValueKind == JsonValueKind.True;
                    bool managerHealthy = health.TryGetProperty("workspace", out JsonElement workspace)
                        && workspace.TryGetProperty("status", out JsonElement managerStatus)
                        && string.Equals(managerStatus.GetString(), "healthy", StringComparison.OrdinalIgnoreCase);
                    string lifecycleStatus = health.TryGetProperty("lifecycle", out lifecycle)
                        && lifecycle.TryGetProperty("status", out JsonElement lifecycleValue)
                            ? lifecycleValue.GetString() ?? "unknown"
                            : "unknown";

                    if (coreReady && managerHealthy)
                    {
                        using var setupResponse = await client.GetAsync(
                            new Uri($"{webUrl}/api/auth/setup/status"),
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken).ConfigureAwait(false);
                        if (setupResponse.IsSuccessStatusCode)
                        {
                            using Stream setupStream = await setupResponse.Content
                                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                            using JsonDocument setupDocument = await JsonDocument.ParseAsync(
                                setupStream,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (setupDocument.RootElement.TryGetProperty("needsSetup", out JsonElement needsSetup)
                                && needsSetup.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            {
                                string firstRun = needsSetup.GetBoolean() ? "administrator setup is required" : "administrator setup is already complete";
                                await log($"Gateway core is ready; {firstRun}. Web UI: {webUrl}.").ConfigureAwait(false);
                                return;
                            }

                            lastError = new InvalidOperationException("The setup endpoint returned an invalid readiness response.");
                        }
                        else
                        {
                            lastError = new InvalidOperationException(
                                $"The setup endpoint returned HTTP {(int)setupResponse.StatusCode} ({setupResponse.ReasonPhrase ?? "unknown"}).");
                        }
                    }
                    else
                    {
                        lastError = new InvalidOperationException(
                            $"Gateway is still starting (lifecycle={lifecycleStatus}; manager={(managerHealthy ? "healthy" : "unhealthy")}).");
                    }
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException("The Gateway health probe timed out.");
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (JsonException ex)
            {
                lastError = new InvalidDataException("The Gateway readiness endpoint returned invalid JSON.", ex);
            }

            DateTime nowUtc = DateTime.UtcNow;
            string progress = lastError?.Message ?? "Gateway is starting";
            if (nowUtc >= nextProgressLogUtc && (progress != previousProgress || nowUtc >= nextHeartbeatUtc))
            {
                string connectorProgress = await ReadGatewayStartupProgressAsync(distroName, startupUtc, cancellationToken)
                    .ConfigureAwait(false);
                await log($"Startup progress: {progress}{(string.IsNullOrWhiteSpace(connectorProgress) ? string.Empty : $"; {connectorProgress}")}")
                    .ConfigureAwait(false);
                previousProgress = progress;
                nextProgressLogUtc = DateTime.UtcNow + progressLogInterval;
                nextHeartbeatUtc = DateTime.UtcNow + heartbeatInterval;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"The appliance did not become ready within {timeout.TotalMinutes:0} minutes. Last probe result: {lastError?.Message ?? "unknown"}.");
    }

    private static async Task<string> ReadGatewayStartupProgressAsync(
        string distroName,
        DateTimeOffset startupUtc,
        CancellationToken cancellationToken)
    {
        string since = startupUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        ProcessResult result = await RunProcessAsync(
            "wsl.exe",
            ["-d", distroName, "-u", "root", "--", "journalctl", "--unit=kautuka-gateway.service", $"--since={since}", "--no-pager", "--output=cat"],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);

        string? line = result.StandardOutput
            .Split(newLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static candidate => candidate.Contains("Built-in MCP bootstrap start.", StringComparison.Ordinal)
                || candidate.Contains("Built-in MCP bootstrap completed.", StringComparison.Ordinal)
                || candidate.Contains("Built-in MCP metadata refresh start.", StringComparison.Ordinal)
                || candidate.Contains("Built-in MCP metadata refresh completed.", StringComparison.Ordinal));
        if (line is null)
        {
            return string.Empty;
        }

        if (line.Contains("metadata refresh start.", StringComparison.Ordinal))
        {
            return $"preparing the first-use connector runtime (an image may need to download): {AfterMarker(line, "connectorId=")}; discovering its tools";
        }

        if (line.Contains("metadata refresh completed.", StringComparison.Ordinal))
        {
            return $"connector tools initialized: {AfterMarker(line, "connectorId=")}";
        }

        if (line.Contains("bootstrap start.", StringComparison.Ordinal))
        {
            return $"loading built-in connector bundle {Path.GetFileName(AfterMarker(line, "bundlePath="))}";
        }

        return $"built-in connector initialized: {AfterMarker(line, "connectorId=")}";
    }

    private static string AfterMarker(string value, string marker)
    {
        int index = value.LastIndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? "unknown" : value[(index + marker.Length)..].Split(' ', 2)[0];
    }

    public static async Task<string?> ReadFirstRunSetupTokenAsync(
        string distroName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroName);

        ProcessResult result = await RunProcessAsync(
            "wsl.exe",
            ["-d", distroName, "-u", "root", "--exec", "/bin/cat", "/var/lib/kautuka/setup-token"],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return null;
        }

        string token = result.StandardOutput.Trim();
        if (token.Length != 64 || token.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The first-run setup token has an unexpected format.");
        }

        return token;
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
