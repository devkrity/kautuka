using Kautuka.Manager.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class TrustedApplianceReleaseFilesTests
{
    [Fact]
    public void ReadsOnlyProtectedVersionedFilesAndKey()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root") return;
        string root = Path.Combine("/root", "kautuka-release-input-" + Guid.NewGuid().ToString("N"));
        string manager = Path.Combine(root, "manager");
        string version = Path.Combine(manager, "versions", "v1");
        string keys = Path.Combine(root, "keys");
        Directory.CreateDirectory(version);
        Directory.CreateDirectory(keys);
        try
        {
            File.WriteAllText(Path.Combine(version, "kautuka-platform-release.json"), "{}");
            File.WriteAllText(Path.Combine(version, "kautuka-platform-release.sig"), "signature");
            File.WriteAllText(Path.Combine(version, "release-id.txt"), "v1\n");
            File.WriteAllText(Path.Combine(keys, "manifest-public-key.pem"), "key");
            string pointer = Path.Combine(manager, "current");
            Directory.CreateSymbolicLink(pointer, "versions/v1");
            string candidate = Path.Combine(manager, "versions", "v2");
            Directory.CreateDirectory(candidate);
            File.Copy(Path.Combine(version, "kautuka-platform-release.json"), Path.Combine(candidate, "kautuka-platform-release.json"));
            File.Copy(Path.Combine(version, "kautuka-platform-release.sig"), Path.Combine(candidate, "kautuka-platform-release.sig"));
            File.WriteAllText(Path.Combine(candidate, "release-id.txt"), "v2\n");
            Assert.Equal("v2", TrustedApplianceReleaseFiles.Read(manager, keys, "v2").InstalledReleaseId);
            Assert.Throws<InvalidDataException>(() => TrustedApplianceReleaseFiles.Read(manager, keys, "../keys"));
            Directory.CreateSymbolicLink(Path.Combine(manager, "versions", "alias"), candidate);
            Assert.Throws<IOException>(() => TrustedApplianceReleaseFiles.Read(manager, keys, "alias"));
            TrustedApplianceReleaseInput actual = TrustedApplianceReleaseFiles.Read(manager, keys);
            Assert.Equal("v1", actual.VersionDirectoryName);
            Assert.Equal("v1", actual.InstalledReleaseId);
            Assert.Equal("signature", actual.SignatureContent);
            Assert.Equal("key", actual.PublicKeyPem);
            Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(actual.ManifestBytes));
            Directory.Delete(pointer);
            Directory.CreateSymbolicLink(pointer, version);
            Assert.Equal("v1", TrustedApplianceReleaseFiles.Read(manager, keys).VersionDirectoryName);
            Directory.Delete(pointer);
            Directory.CreateSymbolicLink(pointer, "versions/../keys");
            Assert.Throws<InvalidDataException>(() => TrustedApplianceReleaseFiles.Read(manager, keys));
            Directory.Delete(pointer);
            Directory.CreateSymbolicLink(pointer, "versions/v1");
            string manifest = Path.Combine(version, "kautuka-platform-release.json");
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
            Assert.Throws<IOException>(() => TrustedApplianceReleaseFiles.Read(manager, keys));
            File.Delete(manifest);
            File.CreateSymbolicLink(manifest, Path.Combine(keys, "manifest-public-key.pem"));
            Assert.Throws<IOException>(() => TrustedApplianceReleaseFiles.Read(manager, keys));
            File.Delete(manifest);
            File.WriteAllText(manifest, new string('x', 1024 * 1024 + 1));
            Assert.Throws<InvalidDataException>(() => TrustedApplianceReleaseFiles.Read(manager, keys));
            File.WriteAllText(manifest, "{}");
            File.Delete(Path.Combine(keys, "manifest-public-key.pem"));
            Assert.Throws<IOException>(() => TrustedApplianceReleaseFiles.Read(manager, keys));
        }
        finally { Directory.Delete(root, true); }
    }
}
