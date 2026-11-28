using System.Reflection;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

namespace Kautuka.IntegrationTests;

public sealed class NativeSqliteAssetTests
{
    [Fact]
    public void DefaultAssetLoadsOnTheNativePlatformWithoutChangingTheDatabaseFormat()
    {
        var method = typeof(SqliteAgentDefinitionStore).GetMethod("GetDefaultSqliteVecPaths", BindingFlags.NonPublic | BindingFlags.Static)!;
        var paths = (IReadOnlyList<string>)method.Invoke(null, null)!;
        string path = Assert.Single(paths);
        string platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        Assert.Contains(platform + "-" + architecture, path, StringComparison.Ordinal);
        Assert.True(File.Exists(path), "The native SQLite vector extension must be included when running from source.");
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        connection.LoadExtension(path);
        using var command = connection.CreateCommand();
        command.CommandText = "select vec_version()";
        Assert.Equal("v0.1.9", command.ExecuteScalar());
    }
}
