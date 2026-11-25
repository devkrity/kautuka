using System.Runtime.InteropServices;
using System.Text.Json;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.Manager.Hosting;

internal sealed class ApplianceRoleImageRetirement(PodmanCommandRunner podman)
{
    internal static async Task<int> RunAsync(string[] obsoleteVersions)
    {
        if (!OperatingSystem.IsLinux()) return 1;
        const string managerRoot = "/opt/kautuka/manager";
        const string keys = "/etc/kautuka";
        try
        {
            var status = TrustedApplianceReleaseFiles.ReadRecoveryStatus(Path.GetDirectoryName(KautukaMaintenanceMode.MarkerPath)!);
            var current = VerifiedApplianceRelease.Verify(TrustedApplianceReleaseFiles.Read(managerRoot, keys));
            if (status.RecoveryRequired || status.Phase != "committed" || status.SnapshotPruneStatus != "succeeded"
                || status.TargetVersion != current.Version || status.SourceVersion == current.Version
                || obsoleteVersions.Contains(current.Version, StringComparer.Ordinal)
                || obsoleteVersions.Contains(status.SourceVersion, StringComparer.Ordinal))
                throw new InvalidDataException("A matching committed appliance recovery pair is required for image retirement.");

            var previous = VerifiedApplianceRelease.Verify(TrustedApplianceReleaseFiles.Read(managerRoot, keys, status.SourceVersion));
            List<VerifiedApplianceRelease> retained = [current, previous];
            var obsolete = new List<VerifiedApplianceRelease>();
            foreach (string directory in Directory.EnumerateDirectories(Path.Combine(managerRoot, "versions")))
            {
                string version = Path.GetFileName(directory);
                if (version.StartsWith('.') || version == current.Version || version == previous.Version) continue;
                var release = VerifiedApplianceRelease.Verify(TrustedApplianceReleaseFiles.Read(managerRoot, keys, version));
                if (obsoleteVersions.Contains(version, StringComparer.Ordinal))
                    obsolete.Add(release);
                else
                    retained.Add(release);
            }
            if (obsolete.Count != obsoleteVersions.Length)
                throw new InvalidDataException("An appliance release selected for retirement could not be verified.");

            await new ApplianceRoleImageRetirement(new PodmanCommandRunner())
                .RetireAsync(obsolete, retained, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or JsonException or KeyNotFoundException or System.Text.DecoderFallbackException)
        {
            await Console.Error.WriteLineAsync($"Appliance images retained: {error.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    internal async Task RetireAsync(IReadOnlyList<VerifiedApplianceRelease> obsolete,
        IReadOnlyList<VerifiedApplianceRelease> retained, CancellationToken cancellationToken)
    {
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(),
        };
        var retainedIds = new HashSet<string>(StringComparer.Ordinal);
        var retainedReferences = new HashSet<string>(StringComparer.Ordinal);
        // The current release and its recovery predecessor must both be usable
        // before we retire anything. Other signed staged releases can be absent.
        for (int i = 0; i < retained.Count; i++)
        {
            foreach ((string role, string reference) in retained[i].Deployment.Images)
            {
                retainedReferences.Add(reference);
                var image = await InspectAsync(role, reference, architecture, cancellationToken).ConfigureAwait(false);
                if (image is not null)
                    retainedIds.Add(image.Value.Id);
                else if (i < 2)
                    throw new InvalidDataException($"Missing retained appliance image {reference}.");
            }
        }

        var oldTags = obsolete.SelectMany(release => release.Deployment.Images.Values
            .Select(reference => reference[..reference.LastIndexOf('@')] + ":" + release.Version))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var release in obsolete)
        {
            foreach ((string role, string reference) in release.Deployment.Images)
            {
                if (retainedReferences.Contains(reference)) continue;
                var image = await InspectAsync(role, reference, architecture, cancellationToken).ConfigureAwait(false);
                if (image is not { } found || retainedIds.Contains(found.Id)
                    || found.Tags is not [var tag] || !oldTags.Contains(tag)
                    || !tag.StartsWith(reference[..reference.LastIndexOf('@')] + ":", StringComparison.Ordinal))
                    continue;
                // Never remove by a mutable tag. Non-forced ID removal refuses
                // a newly added alias or a running or stopped container.
                PodmanCommandResult result = await podman.RunAsync(
                    ["image", "rm", "--no-prune", found.Id], 30, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.ExitCode != 0)
                    await Console.Error.WriteLineAsync($"Retained appliance image {reference}; Podman did not remove it.").ConfigureAwait(false);
            }
        }
    }

    private async Task<(string Id, string[] Tags)?> InspectAsync(string role, string reference,
        string architecture, CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await podman.RunAsync(["image", "inspect", reference], 30, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            PodmanCommandResult exists = await podman.RunAsync(["image", "exists", reference], 30, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (exists.ExitCode == 1 && exists.TerminationReason == ProcessTerminationReason.NonZeroExit)
                return null;
            throw new InvalidOperationException($"Could not determine appliance image identity for {reference}.");
        }

        var expected = new Dictionary<string, string>(StringComparer.Ordinal) { [role] = reference };
        string id = ApplianceRoleImageImporter.ReadInstalledImageIds(result.StdOut, expected, architecture)[role];
        using JsonDocument document = JsonDocument.Parse(result.StdOut);
        JsonElement image = document.RootElement[0];
        if (!image.TryGetProperty("RepoTags", out JsonElement tags) || tags.ValueKind != JsonValueKind.Array)
            return (id, []);
        return (id, tags.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray());
    }
}
