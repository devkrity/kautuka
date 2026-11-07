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

    internal async Task<ResolvedRuntimeImage> ResolveRuntimeImageAsync(string sourceReference, CancellationToken ct)
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
            return await imageStore.ResolveCachedAsync(sourceReference, ct).ConfigureAwait(false);
        RegistryImageIdentity.ValidateSource(sourceReference, platform);
        ContainerImageProvenancePolicy.EnsureTrusted(sourceReference, "Managed runtime image");
        await imageAcquisitionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ImageExistsForAcquisitionAsync(sourceReference, ct).ConfigureAwait(false))
                return await InspectAcquiredImageAsync(sourceReference, sourceReference, platform, ct).ConfigureAwait(false);
            string cacheRoot = Path.Combine(stateRoot, "egress", "registry-images");
            string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceReference + "\n" + platform)));
            string cache = Path.Combine(cacheRoot, key);
            if (Directory.Exists(cache))
                return await LoadAcquiredArchiveAsync(sourceReference, platform, cache, ct).ConfigureAwait(false);
            Uri registry = new("https://" + sourceReference[..sourceReference.IndexOf('/', StringComparison.Ordinal)]);
            if (!options.Grants.Any(grant => grant.ScopeType == "core" && grant.UserId == "system" && grant.AgentId == "management"
                && grant.Role == "materializer" && grant.ComponentId == "image-acquisition" && grant.Destination == registry.Host && grant.Port == registry.Port))
                throw new InvalidOperationException("Registry acquisition is blocked until its exact registry endpoint is approved.");
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
                await RunAcquisitionHelperAsync(helper, staging, ["inspect", "--raw", "--no-creds", "--tls-verify=true", "docker://" + sourceReference], true, ct).ConfigureAwait(false);
                outputMayBeMounted = false;
                byte[] source = await ReadSourceMetadataAsync(staging, ct).ConfigureAwait(false);
                RegistryImageIdentity identity = RegistryImageIdentity.Select(sourceReference, source, platform);
                outputMayBeMounted = true;
                await RunAcquisitionHelperAsync(helper, staging, ["--override-os", "linux", "--override-arch", platform[6..], "copy", "--preserve-digests",
                    "--src-no-creds", "--src-tls-verify=true", "docker://" + identity.SelectedReference, "oci-archive:/output/image.oci:acquired"], false, ct).ConfigureAwait(false);
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

    private async Task RunAcquisitionHelperAsync(ResolvedRuntimeImage helper, string output, IReadOnlyList<string> command, bool metadata, CancellationToken ct)
    {
        string name = "kautuka-image-acquisition-" + Guid.NewGuid().ToString("N");
        PreparedPodmanContainer? prepared = null;
        Exception? failure = null;
        try
        {
            prepared = await PrepareAndAuthorizeAsync(acquisitionIdentity, name, (network, dns) =>
            {
                var args = new List<string> { "create", "--pull=never", "--restart=no", "--name", name, "--label", "kautuka.managed=manager",
                    "--label", "kautuka.role=materializer", "--network", network, "--dns", dns, "--sysctl", "net.ipv6.conf.all.disable_ipv6=1",
                    "--userns=keep-id:uid=1000,gid=1000", "--user=1000:1000", "--cap-drop=ALL", "--security-opt=no-new-privileges",
                    "--read-only", "--pids-limit", "128", "--memory", "536870912", "--tmpfs", "/tmp:rw,nosuid,noexec,size=64m",
                    "--env", "HOME=/tmp", "--volume", output + ":/output:rw,Z", "--entrypoint", "/bin/sh", helper.LocalImageId,
                    "-c", metadata ? "exec /usr/bin/skopeo \"$@\" > /output/source.json" : "exec /usr/bin/skopeo \"$@\"", "skopeo" };
                args.AddRange(command);
                return args;
            }, ct).ConfigureAwait(false);
            PodmanCommandResult started = await StartWithAdmissionAsync(prepared,
                token => runner.RunAsync(prepared.StartArguments(attach: false), 15, token), ct).ConfigureAwait(false);
            if (started.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Start image acquisition", started));
            PodmanCommandResult waited = await runner.RunAsync(["wait", prepared.Id], metadata ? 60 : 600, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (waited.ExitCode != 0 || !int.TryParse(waited.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int exit) || exit != 0)
                throw new InvalidOperationException("The restricted image acquisition helper failed; no direct pull is permitted.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (prepared is not null) await RetireImageHelperAsync(prepared.Id, failure).ConfigureAwait(false);
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

    private async Task<ResolvedRuntimeImage> LoadAcquiredArchiveAsync(string sourceReference, string platform, string directory, CancellationToken ct)
    {
        RegistryImageIdentity identity = RegistryImageIdentity.Select(sourceReference, await ReadSourceMetadataAsync(directory, ct).ConfigureAwait(false), platform);
        string archive = Path.Combine(directory, "image.oci");
        string configId = await identity.VerifyArchiveAsync(archive, ct).ConfigureAwait(false);
        if (!await ImageExistsForAcquisitionAsync(configId, ct).ConfigureAwait(false))
        {
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
}
