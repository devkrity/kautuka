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
    internal async Task EnsureAsync(ApplianceDeploymentDescriptor descriptor, string releaseVersionDirectory, CancellationToken cancellationToken)
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
        SafeFileHandle? images = null;
        try
        {
            foreach (string role in Roles)
            {
                string reference = descriptor.Images[role];
                if (!IsDigestReference(reference)) throw new InvalidDataException("Invalid signed image reference.");
                PodmanCommandResult exists = await runner.RunAsync(["image", "exists", reference], 30, cancellationToken).ConfigureAwait(false);
                if (exists.ExitCode == 1)
                {
                    images ??= TrustedApplianceReleaseFiles.OpenApplianceImagesDirectory(releaseDirectory);
                    using var archive = TrustedApplianceReleaseFiles.OpenApplianceImageArchive(images, role);
                    using var stream = new FileStream(archive, FileAccess.Read);
                    ValidateArchiveIndex(stream, reference);
                    stream.Position = 0;
                    PodmanCommandResult loaded = await runner.RunAsync(["load"], 300, cancellationToken, standardInput: stream).ConfigureAwait(false);
                    if (loaded.ExitCode != 0) throw new InvalidOperationException("Offline appliance image import failed.");
                }
                else if (exists.ExitCode != 0) throw new InvalidOperationException("Podman image lookup failed.");
                await InspectAsync(reference, role, architecture, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { images?.Dispose(); }
    }

#pragma warning restore CA2000
    private async Task InspectAsync(string reference, string role, string architecture, CancellationToken token)
    {
        PodmanCommandResult result = await runner.RunAsync(["image", "inspect", reference], 30, token).ConfigureAwait(false);
        if (result.ExitCode != 0 || !InspectMatches(result.StdOut, reference, role, architecture))
            throw new InvalidDataException("Installed appliance image does not match the signed role, digest and platform.");
    }

    internal static bool InspectMatches(string json, string reference, string role, string architecture)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1) return false;
            JsonElement image = document.RootElement[0];
            if (image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("Digest", out JsonElement digest) || digest.GetString() != reference[(reference.LastIndexOf('@') + 1)..] ||
                !image.TryGetProperty("RepoDigests", out JsonElement references) || references.ValueKind != JsonValueKind.Array ||
                !references.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == reference) ||
                !image.TryGetProperty("Os", out JsonElement os) || os.GetString() != "linux" ||
                !image.TryGetProperty("Architecture", out JsonElement arch) || arch.GetString() != architecture ||
                !image.TryGetProperty("Config", out JsonElement config) || !config.TryGetProperty("Labels", out JsonElement labels) ||
                !labels.TryGetProperty("io.kautuka.service.role", out JsonElement actualRole) || actualRole.GetString() != role) return false;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException) { return false; }
    }

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
