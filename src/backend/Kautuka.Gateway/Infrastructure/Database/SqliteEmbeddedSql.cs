using System.Reflection;

namespace Kautuka.Gateway.Infrastructure.Database;

internal static class SqliteEmbeddedSql
{
    public static readonly string GatewayCurrent = Load("baseline.current.sql");
    public static readonly string AgentStoreCurrent = Load("baseline.agent.current.sql");
    public static readonly string BaselineUsageV1 = Load("baseline.usage.v1.sql");

    private static string Load(string fileName)
    {
        Assembly assembly = typeof(SqliteEmbeddedSql).Assembly;
        string suffix = $".Database.Sql.{fileName}";
        string? resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException($"Embedded SQL resource '{fileName}' was not found.");
        }

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded SQL resource '{resourceName}' could not be opened.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
