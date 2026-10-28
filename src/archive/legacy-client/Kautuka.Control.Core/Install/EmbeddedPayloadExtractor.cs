using System.Reflection;

namespace Kautuka.Control.Core.Install;

/// <summary>
/// Extracts payload binaries embedded as EmbeddedResource into the installer assembly.
/// In local dev builds the resources are absent — <see cref="HasPayloads"/> returns false
/// and no extraction is attempted.
/// </summary>
public static class EmbeddedPayloadExtractor
{
    private static readonly string[] knownPayloads =
    [
        "payload.gateway.exe",
        "payload.channelhost.exe",
        "payload.workspacehost.exe",
        "payload.Kautuka.Control.Desktop.exe",
        "payload.nssm.exe",
    ];

    /// <summary>Returns true if at least one payload binary is embedded in this assembly.</summary>
    public static bool HasPayloads()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        return knownPayloads.Any(p => names.Contains(p));
    }

    /// <summary>
    /// Extracts all known embedded payloads to <paramref name="destDir"/>.
    /// Files that already exist are overwritten.
    /// </summary>
    /// <returns>List of extracted file paths.</returns>
    public static IReadOnlyList<string> ExtractAll(string destDir)
    {
        Directory.CreateDirectory(destDir);

        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var names = new HashSet<string>(asm.GetManifestResourceNames());
        var extracted = new List<string>();

        foreach (var logicalName in knownPayloads)
        {
            if (!names.Contains(logicalName))
                continue;

            // Map logical name → file name: "payload.gateway.exe" → "gateway.exe"
            var fileName = logicalName["payload.".Length..];
            var destPath = Path.Combine(destDir, fileName);

            using var src = asm.GetManifestResourceStream(logicalName)
                             ?? throw new InvalidOperationException(
                                 $"Embedded resource '{logicalName}' not found.");
            using var dest = File.Create(destPath);
            src.CopyTo(dest);

            extracted.Add(destPath);
        }

        return extracted;
    }
}
