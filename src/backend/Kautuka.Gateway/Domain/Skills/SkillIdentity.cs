namespace Kautuka.Gateway.Domain.Skills;

internal static class SkillIdentity
{
    internal static Dictionary<string, T> Index<T>(IEnumerable<T> values, Func<T, string> idSelector)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (T value in values)
        {
            string id = idSelector(value);
            if (string.IsNullOrWhiteSpace(id) || !names.Add(id))
            {
                throw new InvalidOperationException($"Skill id '{id}' is empty, duplicated, or differs only by letter case from another id.");
            }

            index.Add(id, value);
        }

        return index;
    }
}
