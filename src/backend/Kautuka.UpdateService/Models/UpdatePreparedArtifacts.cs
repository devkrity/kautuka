namespace Kautuka.UpdateService.Models;

public sealed record UpdatePreparedArtifacts(
    string TargetReleaseId,
    string PlatformPackagePath,
    string PlatformExtractedPath);
