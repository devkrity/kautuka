using System.Text.Json;

using Kautuka.Control.Core.Api;

namespace Kautuka.Control.Core.Auth;

/// <summary>Loads, saves, and clears the session token at {installDir}/config/session.json.</summary>
public static class SessionStore
{
    private const string fileName = "session.json";

    public static SessionToken? Load(string installDir)
    {
        var path = SessionPath(installDir);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize(bytes, ApiJsonContext.Default.SessionToken);
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Save(string installDir, SessionToken token)
    {
        var path = SessionPath(installDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(token, ApiJsonContext.Default.SessionToken);
        File.WriteAllText(path, json);
    }

    public static void Clear(string installDir)
    {
        var path = SessionPath(installDir);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string SessionPath(string installDir) =>
        Path.Combine(installDir, "config", fileName);
}
