using System.Security.Cryptography;

using Kautuka.Installer.Windows.Installation;
using Kautuka.Installer.Windows.Models;

namespace Kautuka.WindowsInstaller.Tests;

public sealed class InstallerSafetyTests
{
    private static WindowsApplianceInstallState State(string path) => new()
    {
        ReleaseId = "test",
        RootfsPath = path,
        DistroName = "kautuka-test",
        KautukaRoot = path,
        InstallDir = path,
        InstallerExecutablePath = path,
        OwnerSid = "S-1-5-21-test",
        RegistrationId = "{original}"
    };

    [Fact]
    public void ReplacementDistributionWithSameNameAndPathDoesNotMatchOwnership()
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kautuka-ownership-test"));
        var state = State(path);
        Assert.True(WindowsApplianceInstaller.MatchesOwnedRegistration(state, state.OwnerSid, state.DistroName, path, state.RegistrationId));
        Assert.False(WindowsApplianceInstaller.MatchesOwnedRegistration(state, state.OwnerSid, state.DistroName, path, "{replacement}"));
        Assert.False(WindowsApplianceInstaller.MatchesOwnedRegistration(state, "S-1-5-21-other", state.DistroName, path, state.RegistrationId));
        Assert.False(WindowsApplianceInstaller.MatchesOwnedRegistration(state, state.OwnerSid, "unrelated", path, state.RegistrationId));
        Assert.False(WindowsApplianceInstaller.MatchesOwnedRegistration(state, state.OwnerSid, state.DistroName, path + "-other", state.RegistrationId));
    }

    [Fact]
    public void ResumeRejectsArchiveFromUnprotectedDirectoryEvenWhenHashMatches()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "untrusted archive");
            var state = State(path) with { RootfsSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
            Assert.Throws<InvalidOperationException>(() => WindowsApplianceInstaller.WindowsApplianceStateStore.VerifyRootfs(state));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResumeRejectsArchiveReplacedAfterVerification()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kautuka-archive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "rootfs.tar.gz");
        try
        {
            File.WriteAllText(path, "verified image");
            var state = State(path) with { RootfsSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
            WindowsApplianceInstaller.WindowsApplianceStateStore.VerifyRootfsAt(state, directory);
            File.WriteAllText(path, "replaced image");
            Assert.Throws<InvalidOperationException>(() => WindowsApplianceInstaller.WindowsApplianceStateStore.VerifyRootfsAt(state, directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task UninstallRejectsUnownedTargetWithoutDeletingFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "kautuka-safety-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sentinel = Path.Combine(directory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep", TestContext.Current.CancellationToken);
        try
        {
            var request = new WindowsApplianceUninstallRequest { DistroName = "unowned-" + Guid.NewGuid().ToString("N"), InstallDir = directory, DeleteData = true };
            await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsApplianceInstaller.UninstallAsync(request, CancellationToken.None));
            Assert.True(File.Exists(sentinel));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
