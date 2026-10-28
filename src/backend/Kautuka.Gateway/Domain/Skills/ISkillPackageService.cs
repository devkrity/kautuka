namespace Kautuka.Gateway.Domain.Skills;

internal sealed record SkillPackageImportRequest(
    string ArchiveFileName,
    IReadOnlyDictionary<string, string>? SourceMetadata);

internal sealed record SkillPackageInspection(
    SkillDefinition Definition,
    string InstructionsMarkdown,
    IReadOnlyList<string> ResourcePaths,
    IReadOnlyList<string> ScriptPaths,
    IReadOnlyList<SkillScriptInspection> Scripts);

internal sealed record SkillScriptInspection(
    string RelativePath,
    IReadOnlyList<string> ParameterNames);

internal sealed record SkillPackageImportOutcome(
    bool IsBundle,
    SkillPackageInspection? Skill,
    IReadOnlyList<SkillPackageInspection> Skills);

internal interface ISkillPackageService
{
    Task<SkillPackageImportOutcome> ValidateImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken);

    Task<SkillPackageImportOutcome> ImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken);

    SkillPackageInspection Inspect(SkillDefinition skill);

    string ReadResourceText(SkillDefinition skill, string resourcePath);

    string ReadScriptText(SkillDefinition skill, string relativeScriptPath)
        => throw new InvalidOperationException($"Skill '{skill.SkillId}' script '{relativeScriptPath}' was not found.");

    string StagePackageForWorkspace(SkillDefinition skill, string workspaceHostPath, string containerWorkspacePath);

    string ConfinePackagePath(string skillId, string? requestedPath, string? existingPath);

    void DeletePackage(string skillId, string packagePath);
}
