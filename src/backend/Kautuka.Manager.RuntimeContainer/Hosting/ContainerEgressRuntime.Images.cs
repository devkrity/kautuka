using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed partial class ContainerEgressRuntime
{
    private readonly SemaphoreSlim imageAcquisitionGate = new(1, 1);
    private static readonly SourceIdentity acquisitionIdentity = new(new Scope("system", "management", "core"), "materializer", "image-acquisition");

    internal Task<ResolvedRuntimeImage> ResolveRuntimeImageAsync(string sourceReference, CancellationToken ct)
        => ResolveRuntimeImageCoreAsync(sourceReference, allowPreparation: true, ct);

    internal Task<ResolvedRuntimeImage> ResolveCachedRuntimeImageAsync(string sourceReference, CancellationToken ct)
        => ResolveRuntimeImageCoreAsync(sourceReference, allowPreparation: false, ct);

    private async Task<ResolvedRuntimeImage> ResolveRuntimeImageCoreAsync(string sourceReference, bool allowPreparation, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Managed image acquisition requires controlled egress.");
        string platform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux/amd64",
            Architecture.Arm64 => "linux/arm64",
            _ => throw new PlatformNotSupportedException("Unsupported appliance image platform.")
        };
        RuntimeImageEntry? entry = allowlist.Images.FirstOrDefault(image => image.Image == sourceReference);
        // Signed offline assets remain the only bootstrap path. Corrupt assets
        // fail here; they must never become an implicit registry fallback.
        if (entry is not null && (entry.Archive is not null || entry.HasArchiveIdentity))
        {
            ResolvedRuntimeImage released = await imageStore.ResolveCachedAsync(sourceReference, ct, allowLoad: allowPreparation).ConfigureAwait(false);
            if (released.Platform != platform) throw new InvalidOperationException("The released runtime image has the wrong appliance platform.");
            return released;
        }
        RegistryImageIdentity.ValidateSource(sourceReference, platform);
        ContainerImageProvenancePolicy.EnsureTrusted(sourceReference, "Managed runtime image");
        if (!allowPreparation)
        {
            if (await ImageExistsForAcquisitionAsync(sourceReference, ct).ConfigureAwait(false))
                return await InspectAcquiredImageAsync(sourceReference, sourceReference, platform, ct).ConfigureAwait(false);
            string published = RegistryImageCachePath(sourceReference, platform);
            if (Directory.Exists(published))
                return await LoadAcquiredArchiveAsync(sourceReference, platform, published, ct, allowPreparation: false).ConfigureAwait(false);
            throw new InvalidOperationException("The runtime image is not ready; background preparation must finish before execution.");
        }
        await imageAcquisitionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ImageExistsForAcquisitionAsync(sourceReference, ct).ConfigureAwait(false))
                return await InspectAcquiredImageAsync(sourceReference, sourceReference, platform, ct).ConfigureAwait(false);
            string cacheRoot = Path.Combine(stateRoot, "egress", "registry-images");
            string cache = RegistryImageCachePath(sourceReference, platform);
            if (Directory.Exists(cache))
                return await LoadAcquiredArchiveAsync(sourceReference, platform, cache, ct, allowPreparation).ConfigureAwait(false);
            if (!allowPreparation)
                throw new InvalidOperationException("The runtime image is not ready; background preparation must finish before execution.");
            Uri registry = new("https://" + sourceReference[..sourceReference.IndexOf('/', StringComparison.Ordinal)]);
            RequireRegistryApproval(registry.Host, registry.Port);
            await EnsureReadyAsync(ct).ConfigureAwait(false);
            ResolvedRuntimeImage helper = await RequireCachedImageAsync("image-acquisition", ct).ConfigureAwait(false);
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Image acquisition requires the Linux appliance.");
            Directory.CreateDirectory(cacheRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string staging = cache + "-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            bool outputMayBeMounted = false;
            try
            {
                outputMayBeMounted = true;
                await RunAcquisitionHelperAsync(helper, staging, ["inspect", "--raw", "--no-creds", "--tls-verify=true", "docker://" + sourceReference], true,
                    () => outputMayBeMounted = false, ct).ConfigureAwait(false);
                outputMayBeMounted = false;
                byte[] source = await ReadSourceMetadataAsync(staging, ct).ConfigureAwait(false);
                RegistryImageIdentity identity = RegistryImageIdentity.Select(sourceReference, source, platform);
                outputMayBeMounted = true;
                await RunAcquisitionHelperAsync(helper, staging, ["--override-os", "linux", "--override-arch", platform[6..], "copy", "--preserve-digests",
                    "--src-no-creds", "--src-tls-verify=true", "docker://" + identity.SelectedReference, "oci-archive:/output/image.oci:acquired"], false,
                    () => outputMayBeMounted = false, ct).ConfigureAwait(false);
                outputMayBeMounted = false;
                _ = await identity.VerifyArchiveAsync(Path.Combine(staging, "image.oci"), ct).ConfigureAwait(false);
                Directory.Move(staging, cache);
                return await LoadAcquiredArchiveAsync(sourceReference, platform, cache, ct).ConfigureAwait(false);
            }
            finally
            {
                // Preparation can fail before it returns an ID. Preserve output
                // after any such failure rather than infer retirement from the
                // exception type and risk deleting a still-mounted directory.
                if (!outputMayBeMounted && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        finally { imageAcquisitionGate.Release(); }
    }

    private async Task RunAcquisitionHelperAsync(ResolvedRuntimeImage helper, string output, IReadOnlyList<string> command, bool metadata,
        Action confirmRetired, CancellationToken ct)
    {
        string name = "kautuka-image-acquisition-" + Guid.NewGuid().ToString("N");
        PreparedPodmanContainer? prepared = null;
        Exception? failure = null;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            prepared = await PrepareAndAuthorizeAsync(acquisitionIdentity, name, (network, dns) =>
            {
                var args = new List<string> { "create", "--pull=never", "--restart=no", "--name", name, "--label", "kautuka.managed=manager",
                    "--label", "kautuka.role=materializer", "--network", network, "--dns", dns, "--sysctl", "net.ipv6.conf.all.disable_ipv6=1",
                    "--userns=keep-id:uid=1000,gid=1000", "--user=1000:1000", "--cap-drop=ALL", "--security-opt=no-new-privileges",
                    "--read-only", "--read-only-tmpfs=false", "--image-volume=ignore", "--pids-limit", "128", "--memory", "536870912", "--tmpfs", "/tmp:rw,nosuid,noexec",
                    "--env", "HOME=/tmp", "--env", "TMPDIR=/output/.tmp", "--volume", output + ":/output:rw,Z", "--entrypoint", "/bin/sh", helper.LocalImageId,
                    "-c", "set -eu; umask 077; mkdir -p /output/.tmp; exec /usr/bin/skopeo --tmpdir /output/.tmp \"$@\"" + (metadata ? " > /output/source.json" : ""), "skopeo" };
                args.AddRange(command);
                return args;
            }, ct).ConfigureAwait(false);
            PodmanCommandResult started = await StartWithAdmissionAsync(prepared,
                token => runner.RunAsync(prepared.StartArguments(attach: false), 15, token), ct).ConfigureAwait(false);
            if (started.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Start image acquisition", started));
            PodmanCommandResult waited = await runner.RunAsync(["wait", prepared.Id], metadata ? 60 : 600, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (waited.ExitCode != 0 || !int.TryParse(waited.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int exit) || exit != 0)
                throw new InvalidOperationException(DescribeImageAcquisitionFailure(startedAt));
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (prepared is not null)
            {
                await RetireImageHelperAsync(prepared.Id, failure).ConfigureAwait(false);
                confirmRetired();
            }
        }
    }

    private static NetworkAccessIdentity AcquisitionPolicyIdentity => new(acquisitionIdentity.Scope.Kind,
        acquisitionIdentity.Scope.UserId, acquisitionIdentity.Scope.AgentId, acquisitionIdentity.Role, acquisitionIdentity.ComponentId);

    internal void RequireRegistryApproval(string host, int port)
    {
        EgressTcpRule target = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", host, port));
        if (NetworkDestinationPolicy.IsMetadata(target.Destination) || target.Destination == NetworkDestinationPolicy.PlatformControlAddress
            || target.Destination == NetworkDestinationPolicy.PrivateSourceAddress)
            throw new InvalidOperationException("Protected metadata, platform-control and reserved proxy destinations cannot be used as image registries.");
        NetworkAccessIdentity identity = AcquisitionPolicyIdentity;
        if (networkPolicy.EffectiveGrants(options.Grants).Any(grant => NetworkAccessPolicyStore.Identity(grant) == identity
            && grant.Port == target.Port && string.Equals(grant.Destination.TrimEnd('.'), target.Destination, StringComparison.OrdinalIgnoreCase)
            && (grant.AllowPrivateNetwork || !NetworkDestinationPolicy.IsNonPublic(target.Destination)))) return;
        networkPolicy.ObserveDenied(identity, target.Destination, target.Port, DateTimeOffset.UtcNow);
        throw new InvalidOperationException($"Network policy blocks container image downloads from {target.Destination}:{target.Port}. Review this endpoint in Network access, then retry the download.");
    }

    internal string DescribeImageAcquisitionFailure(DateTimeOffset startedAt)
    {
        const string failure = "The restricted image acquisition helper failed; no direct pull is permitted.";
        try
        {
            string[] requested = [.. networkPolicy.Read(new("system", true)).Requests
                .Where(request => request.Identity == AcquisitionPolicyIdentity && request.Status == "pending" && request.LastSeenAt >= startedAt)
                .OrderBy(request => request.Destination, StringComparer.Ordinal).ThenBy(request => request.Port)
                .Take(5).Select(request => request.Destination + ":" + request.Port.ToString(CultureInfo.InvariantCulture))];
            return requested.Length == 0 ? failure : failure + " Network permissions requested during this attempt: "
                + string.Join(", ", requested) + ". Review Network access before retrying; other acquisition errors may also apply.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return failure; // Diagnostics must not hide the original helper failure.
        }
    }

    private async Task RetireImageHelperAsync(string id, Exception? failure)
    {
        try { await RetireAsync(id, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error)
        {
            throw new AggregateException("Image acquisition helper retirement was not confirmed.",
                failure is null ? [error] : [failure, error]);
        }
    }

    private async Task<ResolvedRuntimeImage> LoadAcquiredArchiveAsync(string sourceReference, string platform, string directory, CancellationToken ct, bool allowPreparation = true)
    {
        RegistryImageIdentity identity = RegistryImageIdentity.Select(sourceReference, await ReadSourceMetadataAsync(directory, ct).ConfigureAwait(false), platform);
        string archive = Path.Combine(directory, "image.oci");
        string configId = await identity.VerifyArchiveAsync(archive, ct).ConfigureAwait(false);
        if (!await ImageExistsForAcquisitionAsync(configId, ct).ConfigureAwait(false))
        {
            if (!allowPreparation)
                throw new InvalidOperationException("The runtime image is not ready; background preparation must finish before execution.");
            PodmanCommandResult loaded = await runner.RunAsync(["load", "-i", archive], 300, ct).ConfigureAwait(false);
            if (loaded.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Acquired image load", loaded));
        }
        return await InspectAcquiredImageAsync(sourceReference, configId, platform, ct).ConfigureAwait(false);
    }

    private async Task<ResolvedRuntimeImage> InspectAcquiredImageAsync(string sourceReference, string nativeReference, string platform, CancellationToken ct)
    {
        PodmanCommandResult inspected = await runner.RunAsync(["image", "inspect", nativeReference], 30, ct).ConfigureAwait(false);
        if (inspected.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Acquired image inspection", inspected));
        using JsonDocument document = JsonDocument.Parse(inspected.StdOut);
        if (document.RootElement.GetArrayLength() != 1) throw new InvalidOperationException("Ambiguous acquired image inspection.");
        ResolvedRuntimeImage resolved = ResolvedRuntimeImage.FromInspection(sourceReference, document.RootElement[0]);
        if (resolved.Platform != platform || RuntimeImageEntry.IsLocalId(nativeReference) && resolved.LocalImageId != nativeReference)
            throw new InvalidOperationException("Acquired image identity or platform differs from the selected image.");
        return resolved;
    }

    private async Task<bool> ImageExistsForAcquisitionAsync(string reference, CancellationToken ct)
    {
        PodmanCommandResult result = await runner.RunAsync(["image", "exists", reference], 15, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 1 && result.TerminationReason == ProcessTerminationReason.NonZeroExit) return false;
        throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Acquired image cache query", result));
    }

    private static async Task<byte[]> ReadSourceMetadataAsync(string directory, CancellationToken ct)
    {
        string path = Path.Combine(directory, "source.json");
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidOperationException("Registry metadata exceeds its size limit.");
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    private string RegistryImageCachePath(string sourceReference, string platform)
        => Path.Combine(stateRoot, "egress", "registry-images",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceReference + "\n" + platform))));
}
