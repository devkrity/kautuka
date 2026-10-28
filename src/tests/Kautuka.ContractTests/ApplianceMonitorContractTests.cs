using Kautuka.Monitor;

namespace Kautuka.ContractTests;

public sealed class ApplianceMonitorContractTests
{
    [Fact]
    public void MirroredNetworkingAndDisabledForwardingAreBreaking()
    {
        IReadOnlyList<WslConfigFinding> findings = WslConfigDrift.Evaluate(
            """
            [wsl2]
            localhostForwarding=false
            networkingMode=mirrored
            memory=512MB
            """);

        Assert.Contains(
            findings,
            finding => finding.Key == "wsl2.localhostForwarding" && finding.Severity == WslConfigSeverity.Breaking);
        Assert.Contains(
            findings,
            finding => finding.Key == "wsl2.networkingMode" && finding.Severity == WslConfigSeverity.Breaking);
        Assert.Contains(
            findings,
            finding => finding.Key == "wsl2.memory" && finding.Severity == WslConfigSeverity.Degrading);
    }

    [Fact]
    public void CommentedMirroredNetworkingIsNotAFinding()
    {
        IReadOnlyList<WslConfigFinding> findings = WslConfigDrift.Evaluate(
            """
            [wsl2]
            #networkingMode=mirrored
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void RegistrationUsesLimitedS4UAndATwoMinuteRepeat()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ApplianceTaskPlan plan = ApplianceTaskPlan.Create(
            @"C:\Kautuka\Kautuka.Monitor.exe",
            "kautuka",
            Path.Combine(userProfile, "AppData", "Local", "Kautuka", "WSL", "kautuka"),
            @"DOMAIN\me");
        string script = plan.ToRegistrationScript();

        Assert.Contains("S4U", script, StringComparison.Ordinal);
        Assert.Contains("Limited", script, StringComparison.Ordinal);
        Assert.Contains("-RepetitionInterval", script, StringComparison.Ordinal);
        Assert.Contains("PT2M", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".Repetition.Interval", script, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxValue", script, StringComparison.Ordinal);
        Assert.Contains(@"\Kautuka\", script, StringComparison.Ordinal);
        Assert.Contains("Appliance", script, StringComparison.Ordinal);
        Assert.Contains(@"C:\Kautuka\Kautuka.Monitor.exe", script, StringComparison.Ordinal);
        Assert.Contains("--supervise", script, StringComparison.Ordinal);
        Assert.Contains("--distro-name kautuka", script, StringComparison.Ordinal);
        Assert.Contains("--install-dir", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Highest", script, StringComparison.Ordinal);

        string removal = ApplianceTaskPlan.ToRemovalScript();
        Assert.Contains("Appliance", removal, StringComparison.Ordinal);
        Assert.Contains(@"\Kautuka\", removal, StringComparison.Ordinal);
        Assert.Contains("--supervise", plan.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--supervise", plan.TrayArguments, StringComparison.Ordinal);

        string shortcut = plan.ToShortcutScript(Path.Combine(userProfile, "Desktop", "Kautuka.lnk"));
        Assert.Contains(@"C:\Kautuka\Kautuka.Monitor.exe", shortcut, StringComparison.Ordinal);
        Assert.Contains(plan.TrayArguments, shortcut, StringComparison.Ordinal);
        Assert.DoesNotContain("--supervise", shortcut, StringComparison.Ordinal);
    }
}
