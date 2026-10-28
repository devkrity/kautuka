namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class SkillDefinition
{
    public string SkillId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public DateTimeOffset ImportTimestamp { get; init; } = DateTimeOffset.UtcNow;

    public string PackageHash { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string PackagePath { get; init; } = string.Empty;

    public bool HasResources { get; init; }

    public bool HasScripts { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<string> EnvKeys { get; init; } = [];

    public string Scope { get; init; } = "system";

    public string? OwnerUserId { get; init; }

    public IReadOnlyDictionary<string, string> SourceMetadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

internal enum SkillAttachmentMode
{
    Disabled,
    Trusted,
    RequireApproval,
}

internal sealed class AgentSkillAttachmentDefinition
{
    public string SkillId { get; init; } = string.Empty;

    public SkillAttachmentMode Mode { get; init; } = SkillAttachmentMode.Disabled;
}

internal enum EffectiveSkillSourceScope
{
    Agent,
}

internal sealed class EffectiveSkillManifestEntry
{
    public string SkillId { get; init; } = string.Empty;

    public EffectiveSkillSourceScope SourceScope { get; init; }

    public string? SourceOwnerId { get; init; }

    public SkillAttachmentMode Mode { get; init; } = SkillAttachmentMode.Disabled;

    public bool HasResources { get; init; }

    public bool HasScripts { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string PackageHash { get; init; } = string.Empty;

    public DateTimeOffset ImportTimestamp { get; init; } = DateTimeOffset.UtcNow;
}
