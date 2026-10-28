namespace Kautuka.Gateway.Domain.Skills;

internal static class BuiltInSkillCatalog
{
    public const string KautukaDocsSkillId = "kautuka-docs";

    public static bool IsSystemLockedSkill(string skillId)
        => string.Equals(skillId, KautukaDocsSkillId, StringComparison.OrdinalIgnoreCase);
}
