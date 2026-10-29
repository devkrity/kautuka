using System.Reflection;


namespace Kautuka.Installer.Windows.Hosting;

internal static partial class Program
{
    private static WindowsApplianceLaunchOptions BuildLaunchOptions(string[] args)
        => new()
        {
            InstallDefaults = BuildApplianceInstallRequest(args),
            UninstallDefaults = BuildApplianceUninstallRequest(args),
        };

    private static WindowsApplianceInstallRequest BuildApplianceInstallRequest(string[] args)
    {
        string? releaseTag = TryGetAssemblyMetadata("KautukaReleaseTag");
        string? releaseId = TryGetAssemblyMetadata("KautukaReleaseId") ?? releaseTag;
        string? rootfsPath = TryResolveBundledAssetPath("kautuka-wsl-appliance.tar.gz");
        string? releaseManifestUrl = ResolveDefaultReleaseManifestUrl();
        string? manifestPublicKeyPath = null;
        string releaseChannel = TryGetAssemblyMetadata("KautukaReleaseChannel") ?? "stable";
        string repositoryOwner = "devkrity";
        string repositoryName = "kautuka";
        string? distroName = null;
        string? kautukaRoot = null;
        string? installDir = null;
        var resume = false;
        var allowUnsignedLocalFeeds = false;
        var allowLanAccess = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--release-id":
                    releaseId = ReadNextArg(args, ref index, "--release-id");
                    break;
                case "--rootfs-path":
                    rootfsPath = ReadNextArg(args, ref index, "--rootfs-path");
                    break;
                case "--release-manifest-url":
                    releaseManifestUrl = ReadNextArg(args, ref index, "--release-manifest-url");
                    break;
                case "--manifest-public-key-path":
                    manifestPublicKeyPath = ReadNextArg(args, ref index, "--manifest-public-key-path");
                    break;
                case "--allow-unsigned-local-feeds":
                    allowUnsignedLocalFeeds = true;
                    break;
                case "--release-channel":
                    releaseChannel = ReadNextArg(args, ref index, "--release-channel");
                    break;
                case "--repository-owner":
                    repositoryOwner = ReadNextArg(args, ref index, "--repository-owner");
                    break;
                case "--repository-name":
                    repositoryName = ReadNextArg(args, ref index, "--repository-name");
                    break;
                case "--release-tag":
                    releaseTag = ReadNextArg(args, ref index, "--release-tag");
                    break;
                case "--distro-name":
                    distroName = ReadNextArg(args, ref index, "--distro-name");
                    break;
                case "--kautuka-root":
                    kautukaRoot = ReadNextArg(args, ref index, "--kautuka-root");
                    break;
                case "--install-dir":
                    installDir = ReadNextArg(args, ref index, "--install-dir");
                    break;
                case "--resume":
                    resume = true;
                    break;
                case "--allow-lan":
                    allowLanAccess = true;
                    break;
            }
        }

        if (allowUnsignedLocalFeeds && !IsDevelopmentInstallerContext())
        {
            throw new InvalidOperationException(
                "--allow-unsigned-local-feeds is only permitted for Development or source-checkout installer runs.");
        }

        if (string.IsNullOrWhiteSpace(releaseManifestUrl))
        {
            releaseManifestUrl = IsDevelopmentInstallerContext()
                ? Environment.GetEnvironmentVariable("KAUTUKA_RELEASE_MANIFEST_URL") ?? "http://127.0.0.1:20300/manifest"
                : null;
        }

        manifestPublicKeyPath ??= ResolveDefaultManifestPublicKeyPath();

        var resolvedInstallDir = string.IsNullOrWhiteSpace(installDir)
            ? DeriveInstallDir(distroName ?? "kautuka")
            : installDir;

        return new WindowsApplianceInstallRequest
        {
            ReleaseId = releaseId,
            RootfsPath = rootfsPath,
            ReleaseManifestUrl = releaseManifestUrl,
            ManifestPublicKeyPath = manifestPublicKeyPath,
            AllowUnsignedLocalFeeds = allowUnsignedLocalFeeds,
            ReleaseChannel = releaseChannel,
            RepositoryOwner = repositoryOwner,
            RepositoryName = repositoryName,
            ReleaseTag = releaseTag,
            DistroName = distroName ?? "kautuka",
            KautukaRoot = kautukaRoot,
            InstallDir = resolvedInstallDir,
            Resume = resume,
            AllowLanAccess = allowLanAccess,
        };
    }

    /// <summary>
    /// Parsed independently of <see cref="BuildLaunchOptions"/> because the supervisor runs every
    /// couple of minutes and must not resolve manifest URLs or materialize signing keys to do it.
    /// </summary>
    private static ApplianceSuperviseRequest BuildApplianceSuperviseRequest(string[] args)
    {
        string? distroName = null;
        string? installDir = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--distro-name":
                    distroName = ReadNextArg(args, ref index, "--distro-name");
                    break;
                case "--install-dir":
                    installDir = ReadNextArg(args, ref index, "--install-dir");
                    break;
            }
        }

        var resolvedDistroName = string.IsNullOrWhiteSpace(distroName) ? "kautuka" : distroName;

        return new ApplianceSuperviseRequest
        {
            DistroName = resolvedDistroName,
            InstallDir = string.IsNullOrWhiteSpace(installDir)
                ? DeriveInstallDir(resolvedDistroName)
                : installDir,
        };
    }

    private static WindowsApplianceUninstallRequest BuildApplianceUninstallRequest(string[] args)
    {
        string? distroName = null;
        string? kautukaRoot = null;
        string? installDir = null;
        var deleteData = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--distro-name":
                    distroName = ReadNextArg(args, ref index, "--distro-name");
                    break;
                case "--kautuka-root":
                    kautukaRoot = ReadNextArg(args, ref index, "--kautuka-root");
                    break;
                case "--install-dir":
                    installDir = ReadNextArg(args, ref index, "--install-dir");
                    break;
                case "--delete-data":
                    deleteData = true;
                    break;
            }
        }

        return new WindowsApplianceUninstallRequest
        {
            DistroName = distroName ?? "kautuka",
            InstallDir = string.IsNullOrWhiteSpace(installDir)
                ? DeriveInstallDir(distroName ?? "kautuka")
                : installDir,
            KautukaRoot = kautukaRoot,
            DeleteData = deleteData,
        };
    }

    private static string ReadNextArg(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new InvalidOperationException($"Missing value for {option}.");

        index++;
        return args[index];
    }

    private static string? TryResolveBundledAssetPath(string assetName)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, assetName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolveDefaultReleaseManifestUrl()
    {
        if (IsDevelopmentInstallerContext())
        {
            string? embeddedUrl = TryGetAssemblyMetadata("KautukaReleaseManifestUrl");
            if (!string.IsNullOrWhiteSpace(embeddedUrl))
            {
                return embeddedUrl;
            }

            return Environment.GetEnvironmentVariable("KAUTUKA_RELEASE_MANIFEST_URL")
                ?? "http://127.0.0.1:20300/manifest";
        }

        return null;
    }

    private static string? ResolveDefaultManifestPublicKeyPath()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("KAUTUKA_MANIFEST_PUBLIC_KEY_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        string? bundled = TryResolveBundledAssetPath("manifest-public-key.pem");
        if (!string.IsNullOrWhiteSpace(bundled))
        {
            return bundled;
        }

        return TryMaterializeEmbeddedManifestPublicKey();
    }

    private static string? TryMaterializeEmbeddedManifestPublicKey()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream("Kautuka.Installer.Windows.manifest-public-key.pem");
        if (stream is null)
        {
            return null;
        }

        string directory = Path.Combine(Path.GetTempPath(), "kautuka-installer");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "manifest-public-key.pem");
        using FileStream destinationStream = File.Create(destination);
        stream.CopyTo(destinationStream);
        return destination;
    }

    private static bool IsDevelopmentInstallerContext()
    {
        string? environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            || IsRunningFromSourceCheckout();
    }

    private static bool IsRunningFromSourceCheckout()
    {
        return IsSourceCheckoutRootedAt(AppContext.BaseDirectory)
            || IsSourceCheckoutRootedAt(Environment.CurrentDirectory);
    }

    private static bool IsSourceCheckoutRootedAt(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
        {
            string candidate = Path.Combine(current.FullName, "src", "Directory.Build.props");
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static string DeriveInstallDir(string distroName)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kautuka",
            "WSL",
            distroName);
    }

    private static string? TryGetAssemblyMetadata(string key)
        => Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;
}
