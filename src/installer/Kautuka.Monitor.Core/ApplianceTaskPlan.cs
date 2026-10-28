namespace Kautuka.Monitor;

/// <summary>
/// The scheduled-task definition for the unelevated supervisor. Registration consumes this
/// object so the task cannot drift from the values the tests assert.
/// </summary>
public sealed record ApplianceTaskPlan
{
    public const string TaskFolder = @"\Kautuka\";

    public const string TaskName = "Appliance";

    public const string MonitorFileName = "Kautuka.Monitor.exe";

    public const string RunValueName = "Kautuka.Monitor";

    /// <summary>S4U runs as the installing user with no stored password.</summary>
    public const string LogonType = "S4U";

    /// <summary>
    /// Limited, not Highest. <c>Kautuka.Monitor</c> is <c>asInvoker</c>. Highest would make
    /// every pass elevated and would prompt when the desktop shortcut starts the tray.
    /// </summary>
    public const string RunLevel = "Limited";

    public const string RepetitionInterval = "PT2M";

    public required string ExecutablePath { get; init; }

    public required string Arguments { get; init; }

    /// <summary>Tray launch arguments. They do not include <c>--supervise</c>, which exits without a window.</summary>
    public required string TrayArguments { get; init; }

    public required string UserId { get; init; }

    public static ApplianceTaskPlan Create(
        string monitorExecutable,
        string distroName,
        string installDir,
        string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(distroName);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return new ApplianceTaskPlan
        {
            ExecutablePath = monitorExecutable,
            Arguments = SuperviseArguments(distroName, installDir),
            TrayArguments = TrayArgumentsFor(distroName, installDir),
            UserId = userId,
        };
    }

    public static string SuperviseArguments(string distroName, string installDir)
        => $"--supervise {TrayArgumentsFor(distroName, installDir)}";

    public static string TrayArgumentsFor(string distroName, string installDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroName);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        return $"--distro-name {Quote(distroName)} --install-dir {Quote(installDir)}";
    }

    /// <summary>
    /// Windows PowerShell 5.1 leaves <c>Repetition</c> null unless
    /// <c>-RepetitionInterval</c> is passed to <c>New-ScheduledTaskTrigger</c>.
    /// Assigning <c>Repetition.Interval</c> afterwards throws. A max
    /// <c>TimeSpan</c> duration is rejected by Task Scheduler as out of range;
    /// omitting it repeats until the trigger is unregistered.
    /// </summary>
    public string ToRegistrationScript()
        => $"""
            $ErrorActionPreference = 'Stop'
            $action = New-ScheduledTaskAction -Execute {Ps(ExecutablePath)} -Argument {Ps(Arguments)}
            $boot = New-ScheduledTaskTrigger -AtStartup
            $repeat = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval ([System.Xml.XmlConvert]::ToTimeSpan('{RepetitionInterval}'))
            $principal = New-ScheduledTaskPrincipal -UserId {Ps(UserId)} -LogonType {LogonType} -RunLevel {RunLevel}
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
            Register-ScheduledTask -TaskName {Ps(TaskName)} -TaskPath {Ps(TaskFolder)} -Action $action -Trigger $boot,$repeat -Principal $principal -Settings $settings -Force | Out-Null
            """;

    public static string ToRemovalScript()
        => $"""
            $ErrorActionPreference = 'Stop'
            Unregister-ScheduledTask -TaskName {Ps(TaskName)} -TaskPath {Ps(TaskFolder)} -Confirm:$false -ErrorAction SilentlyContinue
            """;

    public string ToShortcutScript(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        return $"""
            $ErrorActionPreference = 'Stop'
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut({Ps(shortcutPath)})
            $shortcut.TargetPath = {Ps(ExecutablePath)}
            $shortcut.Arguments = {Ps(TrayArguments)}
            $shortcut.WorkingDirectory = {Ps(Path.GetDirectoryName(ExecutablePath) ?? string.Empty)}
            $shortcut.Description = 'Kautuka'
            $shortcut.Save()
            """;
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny([' ', '\t', '"']) < 0)
            return value;

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string Ps(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
