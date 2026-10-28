// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Services/SystemServiceInspector.cs | Namespace: Kautuka.Tui.Features.Services
using System.Text.Json;

using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Processes;

namespace Kautuka.Tui.Features.Services;

public sealed class SystemServiceInspector(ShellCommandRunner commandRunner)
{
    private const string serviceScopeDocumentPath = "/etc/kautuka/service-scope.json";

    private readonly ShellCommandRunner commandRunner = commandRunner;

    public async Task<Result<IReadOnlyList<ServiceUnitStatus>>> GetAllAsync(CancellationToken cancellationToken)
    {
        List<ServiceUnitStatus> statuses = [];

        foreach (ServiceUnitDefinition definition in SystemServiceCatalog.All)
        {
            Result<ServiceUnitStatus> statusResult = await GetAsync(definition, cancellationToken).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Value is null)
            {
                return Results.Failure<IReadOnlyList<ServiceUnitStatus>>(statusResult.ErrorMessage ?? $"Failed to inspect {definition.UnitName}.");
            }

            statuses.Add(statusResult.Value);
        }

        return Results.Success<IReadOnlyList<ServiceUnitStatus>>(statuses);
    }

    public async Task<Result<ServiceUnitStatus>> GetAsync(ServiceUnitDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        (IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environmentVariables) = BuildSystemctlInvocation(definition);

        Result<ShellCommandResult> runResult = await commandRunner.RunAsync(
            "systemctl",
            arguments,
            10,
            cancellationToken,
            environmentVariables).ConfigureAwait(false);

        if (!runResult.IsSuccess || runResult.Value is null)
        {
            return Results.Failure<ServiceUnitStatus>(runResult.ErrorMessage ?? $"Failed to inspect {definition.UnitName}.");
        }

        ShellCommandResult commandResult = runResult.Value;
        if (!commandResult.IsSuccess)
        {
            return Results.Success(new ServiceUnitStatus(
                definition,
                "unknown",
                "unknown",
                string.Empty,
                "unknown",
                definition.DisplayName,
                commandResult.DescribeFailure($"Inspecting {definition.UnitName}"),
                null));
        }

        Dictionary<string, string> values = ParseShowOutput(commandResult.StdOut);
        return Results.Success(new ServiceUnitStatus(
            definition,
            GetValue(values, "LoadState", "unknown"),
            GetValue(values, "ActiveState", "unknown"),
            GetValue(values, "SubState", string.Empty),
            GetValue(values, "UnitFileState", "unknown"),
            GetValue(values, "Description", definition.DisplayName),
            GetValue(values, "FragmentPath", string.Empty),
            ParseSystemdTimestamp(GetValue(values, "ExecMainStartTimestamp", string.Empty))));
    }

    private static (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? EnvironmentVariables) BuildSystemctlInvocation(ServiceUnitDefinition definition)
    {
        List<string> arguments = [];
        Dictionary<string, string>? environmentVariables = null;

        InstalledServiceScope? serviceScope = ReadInstalledServiceScope();
        if (OperatingSystem.IsLinux() && string.Equals(serviceScope?.Scope, "user", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--user");
            environmentVariables = BuildUserSystemdEnvironment(serviceScope);
        }

        arguments.AddRange(
        [
            "show",
            definition.UnitName,
            "--no-pager",
            "--property=Id,LoadState,ActiveState,SubState,UnitFileState,Description,FragmentPath,ExecMainStartTimestamp",
        ]);

        return (arguments, environmentVariables);
    }

    private static Dictionary<string, string>? BuildUserSystemdEnvironment(InstalledServiceScope? serviceScope)
    {
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            string? userName = serviceScope?.Username;
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = Environment.GetEnvironmentVariable("USER");
            }

            if (!string.IsNullOrWhiteSpace(userName)
                && TryResolveUserId(userName, out int userId))
            {
                runtimeDirectory = $"/run/user/{userId}";
            }
        }

        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            return null;
        }

        Dictionary<string, string> environmentVariables = new(StringComparer.Ordinal)
        {
            ["XDG_RUNTIME_DIR"] = runtimeDirectory,
        };

        string? existingBusAddress = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        environmentVariables["DBUS_SESSION_BUS_ADDRESS"] = string.IsNullOrWhiteSpace(existingBusAddress)
            ? $"unix:path={runtimeDirectory}/bus"
            : existingBusAddress;

        return environmentVariables;
    }

    private static InstalledServiceScope? ReadInstalledServiceScope()
    {
        if (!File.Exists(serviceScopeDocumentPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(serviceScopeDocumentPath));
            JsonElement root = document.RootElement;

            string? scope = root.TryGetProperty("scope", out JsonElement scopeElement) && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString()
                : null;
            string? username = root.TryGetProperty("username", out JsonElement usernameElement) && usernameElement.ValueKind == JsonValueKind.String
                ? usernameElement.GetString()
                : null;

            return new InstalledServiceScope(scope, username);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool TryResolveUserId(string userName, out int userId)
    {
        userId = 0;

        foreach (string line in File.ReadLines("/etc/passwd"))
        {
            string[] parts = line.Split(':');
            if (parts.Length < 3 || !string.Equals(parts[0], userName, StringComparison.Ordinal))
            {
                continue;
            }

            return int.TryParse(parts[2], out userId);
        }

        return false;
    }

    private static Dictionary<string, string> ParseShowOutput(string output)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line[..separatorIndex];
            string value = line[(separatorIndex + 1)..];
            values[key] = value;
        }

        return values;
    }

    private static string GetValue(Dictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static DateTimeOffset? ParseSystemdTimestamp(string raw)
    {
        // systemd returns timestamps like "Mon 2026-05-25 08:30:00 UTC" or empty/"n/a"
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("n/a", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Strip the leading weekday abbreviation ("Mon ", "Tue ", etc.) and parse
        ReadOnlySpan<char> span = raw.AsSpan();
        int spaceIndex = span.IndexOf(' ');
        if (spaceIndex > 0 && spaceIndex <= 4)
        {
            span = span[(spaceIndex + 1)..];
        }

        return DateTimeOffset.TryParse(span, out DateTimeOffset result) ? result : null;
    }

    private sealed record InstalledServiceScope(string? Scope, string? Username);
}

public sealed record ServiceUnitStatus(
    ServiceUnitDefinition Definition,
    string LoadState,
    string ActiveState,
    string SubState,
    string UnitFileState,
    string Description,
    string Detail,
    DateTimeOffset? ExecMainStartTime);
