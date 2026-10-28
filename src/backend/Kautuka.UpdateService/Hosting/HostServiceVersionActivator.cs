namespace Kautuka.UpdateService.Hosting;

internal static class HostServiceVersionActivator
{
    internal static readonly string[] HostServiceKeys = ["gateway", "credentialshost", "channelhost", "webhost", "updateservice"];
    internal static readonly string[] VersionedPayloadKeys = [.. HostServiceKeys, "tui"];

    private const string currentInstallFolderName = "current";
    private const string versionInstallFolderName = "versions";

    public static void ActivateManagerAndHostServices(string managerInstallRoot, string targetReleaseId, string? extractedPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managerInstallRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetReleaseId);
        ArgumentNullException.ThrowIfNull(logger);

        logger.LogInformation(
            "Starting host service activation for release {ReleaseId}. ManagerInstallRoot={ManagerInstallRoot}, ExtractedPath={ExtractedPath}",
            targetReleaseId,
            managerInstallRoot,
            extractedPath ?? "<null>");

        ActivateManagerVersion(managerInstallRoot, targetReleaseId, extractedPath, logger);
        ActivateSiblingServiceVersions(managerInstallRoot, targetReleaseId, extractedPath, logger);

        logger.LogInformation(
            "Completed host service activation for release {ReleaseId}.",
            targetReleaseId);
    }

    internal static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, file);
            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void ActivateManagerVersion(string managerInstallRoot, string targetReleaseId, string? extractedPath, ILogger logger)
    {
        logger.LogInformation(
            "Activating Manager version for release {ReleaseId}. InstallRoot={InstallRoot}, ExtractedPath={ExtractedPath}",
            targetReleaseId,
            managerInstallRoot,
            extractedPath ?? "<null>");

        if (TryActivateVersionedInstall(managerInstallRoot, targetReleaseId, extractedPath, ResolveExtractedManagerContentRoot, logger))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(extractedPath))
        {
            throw new InvalidOperationException("Rollback requires a versioned Manager install layout.");
        }

        string contentRoot = ResolveExtractedManagerContentRoot(extractedPath);
        logger.LogWarning(
            "Manager install root {InstallRoot} is not a versioned symlink layout. Falling back to in-place copy from {ContentRoot}.",
            managerInstallRoot,
            contentRoot);
        CopyDirectoryContents(contentRoot, managerInstallRoot);
    }

    private static void ActivateSiblingServiceVersions(string managerInstallRoot, string targetReleaseId, string? extractedPath, ILogger logger)
    {
        string applianceRoot = ResolveApplianceRoot(managerInstallRoot);
        logger.LogInformation(
            "Activating sibling services for release {ReleaseId}. ApplianceRoot={ApplianceRoot}",
            targetReleaseId,
            applianceRoot);

        foreach (string serviceKey in VersionedPayloadKeys)
        {
            string currentInstallPath = Path.Combine(applianceRoot, serviceKey, currentInstallFolderName);
            string? extractedServicePath = ResolveExtractedPayloadContentRoot(extractedPath, serviceKey);

            logger.LogInformation(
                "Evaluating service {ServiceKey}. CurrentInstallPath={CurrentInstallPath}, ExtractedServicePath={ExtractedServicePath}",
                serviceKey,
                currentInstallPath,
                extractedServicePath ?? "<null>");

            if (string.IsNullOrWhiteSpace(extractedServicePath)
                && !Directory.Exists(currentInstallPath)
                && !Directory.Exists(Path.GetDirectoryName(currentInstallPath)!))
            {
                logger.LogInformation(
                    "Skipping service {ServiceKey} because neither extracted payload nor install root exists.",
                    serviceKey);
                continue;
            }

            if (string.Equals(serviceKey, "updateservice", StringComparison.Ordinal)
                && string.Equals(ResolveCurrentReleaseId(currentInstallPath), targetReleaseId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Skipping service {ServiceKey} activation because the running staged updater is already on release {ReleaseId}.",
                    serviceKey,
                    targetReleaseId);
                continue;
            }

            if (TryActivateVersionedInstall(currentInstallPath, targetReleaseId, extractedServicePath, static path => path, logger))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(extractedServicePath))
            {
                throw new InvalidOperationException($"Rollback requires a versioned install layout for host service '{serviceKey}'.");
            }

            logger.LogWarning(
                "Service {ServiceKey} is not using a versioned symlink layout. Falling back to in-place copy from {ExtractedServicePath} to {CurrentInstallPath}.",
                serviceKey,
                extractedServicePath,
                currentInstallPath);
            CopyDirectoryContents(extractedServicePath, currentInstallPath);
        }
    }

    private static string ResolveApplianceRoot(string managerInstallRoot)
    {
        DirectoryInfo managerCurrentDirectory = new(managerInstallRoot);
        DirectoryInfo? managerServiceDirectory = managerCurrentDirectory.Parent;
        DirectoryInfo? applianceRoot = managerServiceDirectory?.Parent;

        return applianceRoot?.FullName
            ?? throw new InvalidOperationException($"Manager install root '{managerInstallRoot}' does not have the expected appliance layout.");
    }

    private static string? ResolveCurrentReleaseId(string installRoot)
    {
        DirectoryInfo installDirectory = new(installRoot);
        string? linkTarget = installDirectory.LinkTarget;

        if (!string.IsNullOrWhiteSpace(linkTarget))
        {
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(linkTarget));
        }

        string resolvedPath = Path.GetFullPath(installRoot);
        DirectoryInfo resolvedDirectory = new(resolvedPath);
        DirectoryInfo? parentDirectory = resolvedDirectory.Parent;
        if (parentDirectory is not null && string.Equals(parentDirectory.Name, versionInstallFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return resolvedDirectory.Name;
        }

        return null;
    }

    private static bool TryActivateVersionedInstall(string installRoot, string targetReleaseId, string? extractedPath, Func<string, string> resolveContentRoot, ILogger logger)
    {
        DirectoryInfo installDirectory = new(installRoot);
        DirectoryInfo? parentDirectory = installDirectory.Parent;
        if (parentDirectory is null)
        {
            logger.LogWarning(
                "Install root {InstallRoot} has no parent directory. Cannot use versioned install activation.",
                installRoot);
            return false;
        }

        string? linkTarget = installDirectory.LinkTarget;
        bool isReparsePoint = Directory.Exists(installDirectory.FullName)
            && File.GetAttributes(installDirectory.FullName).HasFlag(FileAttributes.ReparsePoint);
        bool symlinkLike = linkTarget is not null
            || (Directory.Exists(installDirectory.FullName)
                && isReparsePoint);

        logger.LogInformation(
            "Inspecting install root {InstallRoot}. Exists={Exists}, LinkTarget={LinkTarget}, IsReparsePoint={IsReparsePoint}, SymlinkLike={SymlinkLike}",
            installRoot,
            Directory.Exists(installDirectory.FullName),
            linkTarget ?? "<null>",
            isReparsePoint,
            symlinkLike);

        if (!symlinkLike)
        {
            return false;
        }

        ReleaseTrustPolicy.EnsureSafeReleaseId(targetReleaseId);
        string versionsRoot = Path.Combine(parentDirectory.FullName, versionInstallFolderName);
        string targetVersionRoot = Path.Combine(versionsRoot, targetReleaseId);

        if (!string.IsNullOrWhiteSpace(extractedPath))
        {
            string contentRoot = resolveContentRoot(extractedPath);
            logger.LogInformation(
                "Preparing version root {TargetVersionRoot} from extracted content {ContentRoot}.",
                targetVersionRoot,
                contentRoot);
            if (Directory.Exists(targetVersionRoot))
            {
                logger.LogInformation(
                    "Removing existing target version root {TargetVersionRoot} before refresh.",
                    targetVersionRoot);
                Directory.Delete(targetVersionRoot, recursive: true);
            }

            CopyDirectoryContents(contentRoot, targetVersionRoot);
        }

        if (!Directory.Exists(targetVersionRoot))
        {
            logger.LogError(
                "Target version directory {TargetVersionRoot} does not exist after preparation.",
                targetVersionRoot);
            throw new InvalidOperationException($"Target version directory '{targetVersionRoot}' does not exist.");
        }

        string nextLinkPath = Path.Combine(parentDirectory.FullName, $"{currentInstallFolderName}.next");
        logger.LogInformation(
            "Switching install root {InstallRoot} to target version {TargetVersionRoot} via next link {NextLinkPath}.",
            installRoot,
            targetVersionRoot,
            nextLinkPath);
        DeletePathIfExists(nextLinkPath);
        Directory.CreateSymbolicLink(nextLinkPath, targetVersionRoot);

        DeletePathIfExists(installDirectory.FullName);
        Directory.Move(nextLinkPath, installDirectory.FullName);
        logger.LogInformation(
            "Install root {InstallRoot} now points to release {ReleaseId}.",
            installRoot,
            targetReleaseId);
        return true;
    }

    private static string ResolveExtractedManagerContentRoot(string extractedPath)
    {
        string binPath = Path.Combine(extractedPath, "bin");
        return Directory.Exists(binPath) ? binPath : extractedPath;
    }

    private static string? ResolveExtractedPayloadContentRoot(string? extractedPath, string serviceKey)
    {
        if (string.IsNullOrWhiteSpace(extractedPath))
        {
            return null;
        }

        if (string.Equals(serviceKey, "tui", StringComparison.Ordinal))
        {
            string[] candidatePaths =
            [
                Path.Combine(extractedPath, "tools", "tui"),
                Path.Combine(extractedPath, "manager", "tools", "tui"),
            ];

            return candidatePaths.FirstOrDefault(Directory.Exists);
        }

        string payloadPath = Path.Combine(extractedPath, "services", serviceKey);
        return Directory.Exists(payloadPath) ? payloadPath : null;
    }

    private static void DeletePathIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
