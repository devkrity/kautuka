using System.Reflection;

using Kautuka.NetworkAccessController;
using Kautuka.Telemetry.Security;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.IntegrationTests;

public sealed class NativeDirectoryFlagTests
{
    [Fact]
    public void PolicyAndCertificateFolderFlagsOpenDirectoriesAndRejectRegularFiles()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux directory-open flags are tested against the native kernel.");
            return;
        }
        string root = Directory.CreateTempSubdirectory().FullName;
        string file = Path.Combine(root, "regular");
        File.WriteAllText(file, "data");
        try
        {
            foreach (Type owner in new[] { typeof(NetworkAccessPolicyStore), typeof(GatewayConnectCertificateMaterial) })
            {
                var field = owner.GetField("LinuxDirectoryFlags", BindingFlags.Static | BindingFlags.NonPublic)!;
                var open = owner.GetMethod("open", BindingFlags.Static | BindingFlags.NonPublic)!;
                int flags = (int)field.GetValue(null)!;
                int directoryFd = (int)open.Invoke(null, [root, flags])!;
                Assert.True(directoryFd >= 0, $"{owner.Name} could not open a directory with its native flags.");
                using var directory = new SafeFileHandle(directoryFd, ownsHandle: true);
                int fileFd = (int)open.Invoke(null, [file, flags])!;
                if (fileFd >= 0)
                {
                    using var unexpected = new SafeFileHandle(fileFd, ownsHandle: true);
                    Assert.Fail($"{owner.Name} accepted a regular file as a directory.");
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
