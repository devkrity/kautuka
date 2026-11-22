using System.Buffers;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text.Json;

using Kautuka.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Win32.SafeHandles;


namespace Kautuka.Manager.Hosting;

/// <summary>Imports only signed appliance service images from protected local archives.</summary>
internal sealed class ApplianceRoleImageImporter
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdef");
    private static readonly string[] Roles = ["credentialshost", "channelhost", "gateway", "webhost", "updateservice"];
    private readonly PodmanCommandRunner runner;

    internal ApplianceRoleImageImporter(PodmanCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        this.runner = runner;
    }

#pragma warning disable CA2000 // FileStream and its owned handle are held through Podman load and disposed by using declarations.
    internal async Task<Dictionary<string, string>> EnsureAsync(
        ApplianceDeploymentDescriptor descriptor, string releaseVersionDirectory, CancellationToken cancellationToken,
        bool requireVerificationFence = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (descriptor.SchemaVersion != 1 || descriptor.HookAbi <= 0 || descriptor.Images.Count != Roles.Length || Roles.Any(role => !descriptor.Images.ContainsKey(role)))
            throw new InvalidDataException("Invalid verified appliance deployment.");
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Unsupported appliance image platform."),
        };
        using SafeFileHandle releaseDirectory = TrustedApplianceReleaseFiles.OpenApplianceReleaseDirectory(releaseVersionDirectory);
        string[] references = Roles.Select(role => descriptor.Images[role]).ToArray();
        if (references.Any(reference => !IsDigestReference(reference)))
            throw new InvalidDataException("Invalid signed image reference.");

        // Warm starts and config-only candidates validate the entire cohort in one Podman call.
        PodmanCommandResult installed = await runner.RunAsync(["image", "inspect", .. references], 30, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (installed.ExitCode == 0)
            return ReadInstalledImageIds(installed.StdOut, descriptor.Images, architecture, requireVerificationFence);

        SafeFileHandle? images = null;
        try
        {
            for (int index = 0; index < Roles.Length; index++)
            {
                string reference = references[index];
                PodmanCommandResult exists = await runner.RunAsync(["image", "exists", reference], 30, cancellationToken).ConfigureAwait(false);
                if (exists.ExitCode == 1)
                {
                    string role = Roles[index];
                    try
                    {
                        images ??= TrustedApplianceReleaseFiles.OpenApplianceImagesDirectory(releaseDirectory);
                        using var archive = TrustedApplianceReleaseFiles.OpenApplianceImageArchive(images, role);
                        using var stream = new FileStream(archive, FileAccess.Read);
                        ValidateArchiveIndex(stream, reference);
                        stream.Position = 0;
                        PodmanCommandResult loaded = await runner.RunAsync(["load"], 300, cancellationToken, standardInput: stream).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (loaded.ExitCode != 0)
                            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure($"Offline appliance image import for {role}", loaded));
                    }
                    catch (IOException error)
                    {
                        throw new InvalidDataException($"The signed {role} image is missing from Podman and its staged archive is unavailable or unreadable.", error);
                    }
                }
                else if (exists.ExitCode != 0) throw new InvalidOperationException("Podman image lookup failed.");
            }
        }
        finally { images?.Dispose(); }

        installed = await runner.RunAsync(["image", "inspect", .. references], 30, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (installed.ExitCode != 0)
            throw new InvalidDataException("Could not inspect the installed appliance images.");
        return ReadInstalledImageIds(installed.StdOut, descriptor.Images, architecture, requireVerificationFence);
    }

#pragma warning restore CA2000
    internal static Dictionary<string, string> ReadInstalledImageIds(
        string json, IReadOnlyDictionary<string, string> references, string architecture, bool requireVerificationFence = false)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement entries = document.RootElement;
        const string mismatch = "Installed appliance images do not match the signed roles, digests and platform.";
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != references.Count)
            throw new InvalidDataException(mismatch);
        var imageIds = new Dictionary<string, string>(references.Count, StringComparer.Ordinal);
        foreach ((string role, string reference) in references)
        {
            JsonElement image = entries.EnumerateArray().FirstOrDefault(entry => ImageMatches(entry, reference, role, architecture));
            if (image.ValueKind == JsonValueKind.Undefined ||
                !image.TryGetProperty("Id", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
                !TryNormalizeImageId(id.GetString(), out string canonical))
                throw new InvalidDataException(mismatch);
            if (requireVerificationFence &&
                (!image.GetProperty("Config").GetProperty("Labels").TryGetProperty("io.kautuka.service.appliance-fence", out JsonElement fence) ||
                 !IsString(fence, "1")))
                throw new InvalidDataException($"The signed appliance image for {role} does not support fenced verification.");
            imageIds.Add(role, canonical);
        }
        return imageIds;
    }

    internal static bool TryNormalizeImageId(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (value is null) return false;
        string hex = value.StartsWith("sha256:", StringComparison.Ordinal) ? value["sha256:".Length..] : value;
        if (hex.Length != 64 || hex.AsSpan().IndexOfAnyExcept(HexDigits) >= 0) return false;
        canonical = $"sha256:{hex}";
        return true;
    }

    private static bool ImageMatches(JsonElement image, string reference, string role, string architecture)
        => image.ValueKind == JsonValueKind.Object
            && image.TryGetProperty("Digest", out JsonElement digest) && IsString(digest, reference[(reference.LastIndexOf('@') + 1)..])
            && image.TryGetProperty("RepoDigests", out JsonElement references) && references.ValueKind == JsonValueKind.Array
            && references.EnumerateArray().Any(item => IsString(item, reference))
            && image.TryGetProperty("Os", out JsonElement os) && IsString(os, "linux")
            && image.TryGetProperty("Architecture", out JsonElement arch) && IsString(arch, architecture)
            && image.TryGetProperty("Config", out JsonElement config) && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("Labels", out JsonElement labels) && labels.ValueKind == JsonValueKind.Object
            && labels.TryGetProperty("io.kautuka.service.role", out JsonElement actualRole) && IsString(actualRole, role);

    private static bool IsString(JsonElement value, string expected)
        => value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    internal static void ValidateArchiveIndex(Stream stream, string reference)
    {
        using var tar = new TarReader(stream, leaveOpen: true);
        JsonDocument? index = null;
        try
        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                string name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                if (name != "index.json") continue;
                if (index is not null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Length > 1024 * 1024 || entry.DataStream is null)
                    throw new InvalidDataException("Invalid OCI archive index.");
                index = JsonDocument.Parse(entry.DataStream);
            }
            if (index is null || !index.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) || schema.GetInt32() != 2 ||
                !index.RootElement.TryGetProperty("manifests", out JsonElement manifests) || manifests.ValueKind != JsonValueKind.Array || manifests.GetArrayLength() != 1 ||
                !manifests[0].TryGetProperty("digest", out JsonElement digest) || digest.GetString() != reference[(reference.LastIndexOf('@') + 1)..])
                throw new InvalidDataException("OCI archive does not contain the signed manifest digest.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("Invalid OCI archive index.", ex);
        }
        finally { index?.Dispose(); }
    }

    private static bool IsDigestReference(string value)
    {
        int separator = value.LastIndexOf("@sha256:", StringComparison.Ordinal);
        return separator > 0 && value.Length == separator + 72 && value.AsSpan(separator + 8).IndexOfAnyExcept(HexDigits) < 0 &&
            value.AsSpan(0, separator).Contains('/');
    }
}
