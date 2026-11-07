using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.Fixtures;

internal static class ImageArchiveManagerFixture
{
    internal static async Task RunAsync(JsonElement input, PodmanCommandRunner native, JsonObject report, CancellationToken ct)
    {
        JsonElement data = input.GetProperty("archiveProof");
        string root = data.GetProperty("root").GetString()!;
        var entry = new RuntimeImageEntry("archive-proof", data.GetProperty("source").GetString()!, "image.oci",
            data.GetProperty("sha256").GetString()!, data.GetProperty("platform").GetString()!, data.GetProperty("localImageId").GetString()!);
        int loads = 0;
        var runner = new PodmanCommandRunner(async (args, timeout, token, environment) =>
        {
            if (args[0] == "load") Interlocked.Increment(ref loads);
            return await native.RunAsync(args, timeout, token, environment);
        });
        var owner = new PodmanImageArchiveStore(runner, root, new RuntimeImageAllowlist(1, [entry]), NullLogger<PodmanImageArchiveStore>.Instance);
        PodmanCommandResult before = await native.RunAsync(["image", "exists", entry.LocalImageId!], 10, ct);
        if (before.ExitCode != 1) throw new InvalidOperationException("The cold archive fixture image was already present.");
        ResolvedRuntimeImage resolved = await owner.ResolveCachedAsync(entry.Image, ct);
        if (resolved.LocalImageId != entry.LocalImageId || resolved.SourceReference != entry.Image || resolved.Platform != entry.Platform || Volatile.Read(ref loads) != 1)
            throw new InvalidOperationException("Native OCI load did not preserve the signed source/config/platform binding.");
        PodmanCommandResult source = await native.RunAsync(["image", "exists", entry.Image], 10, ct);
        if (source.ExitCode != 1) throw new InvalidOperationException("The archive fixture unexpectedly retained repository digest metadata.");
        PodmanCommandResult removed = await native.RunAsync(["rmi", resolved.LocalImageId], 15, ct);
        if (removed.ExitCode != 0) throw new InvalidOperationException("Cannot retire the isolated archive proof image.");
        if ((await native.RunAsync(["image", "exists", resolved.LocalImageId], 10, ct)).ExitCode != 1)
            throw new InvalidOperationException("The archive proof image remained cached after retirement.");
        await File.AppendAllTextAsync(Path.Combine(root, entry.Archive!), "tampered", ct);
        bool rejected = false;
        try { await owner.ResolveCachedAsync(entry.Image, ct); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || Volatile.Read(ref loads) != 1) throw new InvalidOperationException("The modified archive reached Podman load.");
        report["coldArchiveVerifiedAndMappedWithoutRepositoryMetadata"] = true;
        report["coldArchiveTamperRejectedBeforeLoad"] = true;
        await Console.Error.WriteLineAsync("fixture: native cold OCI identity mapping and hash rejection passed");
    }
}
