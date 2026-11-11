using System.Text.Json;

namespace Kautuka.NetworkAccessController;

/// <summary>
/// Stores administrator-approved image compatibility profiles (<c>image-compat.json</c>)
/// keyed strictly by image digest (<c>sha256:&lt;64 hex&gt;</c>) and restricted to the three
/// sanctioned relaxations: <c>writable-rootfs</c>, <c>root-user</c>, <c>file-ownership-caps</c>.
/// </summary>
public sealed class ImageCompatibilityStore
{
    public const string WritableRootfs = "writable-rootfs";
    public const string RootUser = "root-user";
    public const string FileOwnershipCaps = "file-ownership-caps";

    private static readonly HashSet<string> allowedRelaxations = new(StringComparer.Ordinal)
    {
        WritableRootfs,
        RootUser,
        FileOwnershipCaps,
    };

    private readonly object gate = new();
    private readonly string path;
    private ImageCompatState? state;

    public ImageCompatibilityStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public static IReadOnlySet<string> AllowedRelaxations => allowedRelaxations;

    public ImageCompatState GetState(NetworkAccessActor actor)
    {
        EnsureAdmin(actor);
        lock (gate)
        {
            return Load();
        }
    }

    public ImageCompatProfile? GetProfileByDigest(string imageDigest)
    {
        string normalizedDigest = ValidateAndNormalizeDigest(imageDigest);
        lock (gate)
        {
            return Load().Profiles.FirstOrDefault(p => string.Equals(p.ImageDigest, normalizedDigest, StringComparison.Ordinal));
        }
    }

    public ImageCompatProfile SetProfile(
        NetworkAccessActor actor,
        string imageDigest,
        IReadOnlyList<string> relaxations,
        string? connectorId = null,
        string? connectorDisplayName = null,
        string? reason = null)
    {
        EnsureAdmin(actor);
        string normalizedDigest = ValidateAndNormalizeDigest(imageDigest);
        string[] validatedRelaxations = ValidateRelaxations(relaxations);

        lock (gate)
        {
            ImageCompatState current = Load();
            var profile = new ImageCompatProfile(
                normalizedDigest,
                string.IsNullOrWhiteSpace(connectorId) ? null : connectorId.Trim(),
                string.IsNullOrWhiteSpace(connectorDisplayName) ? null : connectorDisplayName.Trim(),
                validatedRelaxations,
                actor.UserId,
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

            ImageCompatProfile[] nextProfiles =
            [
                .. current.Profiles.Where(p => !string.Equals(p.ImageDigest, normalizedDigest, StringComparison.Ordinal)),
                profile,
            ];
            ImageCompatRequest[] nextPending =
            [
                .. current.PendingRequests.Where(r => !string.Equals(r.ImageDigest, normalizedDigest, StringComparison.Ordinal)),
            ];

            Save(new ImageCompatState(1, nextProfiles, nextPending));
            return profile;
        }
    }

    public bool DeleteProfile(NetworkAccessActor actor, string imageDigest)
    {
        EnsureAdmin(actor);
        string normalizedDigest = ValidateAndNormalizeDigest(imageDigest);
        lock (gate)
        {
            ImageCompatState current = Load();
            int before = current.Profiles.Count;
            ImageCompatProfile[] nextProfiles =
            [
                .. current.Profiles.Where(p => !string.Equals(p.ImageDigest, normalizedDigest, StringComparison.Ordinal)),
            ];

            if (nextProfiles.Length == before)
            {
                return false;
            }

            Save(current with { Profiles = nextProfiles });
            return true;
        }
    }

    public ImageCompatRequest RecordPendingRequest(
        string imageDigest,
        string connectorId,
        string? connectorDisplayName,
        string failureReason)
    {
        string normalizedDigest = ValidateAndNormalizeDigest(imageDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        lock (gate)
        {
            ImageCompatState current = Load();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ImageCompatRequest? existing = current.PendingRequests.FirstOrDefault(
                r => string.Equals(r.ImageDigest, normalizedDigest, StringComparison.Ordinal));
            var request = new ImageCompatRequest(
                normalizedDigest,
                connectorId.Trim(),
                string.IsNullOrWhiteSpace(connectorDisplayName) ? existing?.ConnectorDisplayName : connectorDisplayName.Trim(),
                string.IsNullOrWhiteSpace(failureReason) ? "Container requires compatibility mode." : failureReason.Trim(),
                existing?.FirstSeenUtc ?? now,
                now);

            ImageCompatRequest[] nextPending =
            [
                .. current.PendingRequests.Where(r => !string.Equals(r.ImageDigest, normalizedDigest, StringComparison.Ordinal)),
                request,
            ];
            Save(current with { PendingRequests = nextPending });
            return request;
        }
    }

    public static string ValidateAndNormalizeDigest(string? imageDigest)
    {
        if (string.IsNullOrWhiteSpace(imageDigest))
        {
            throw new ArgumentException("invalid_digest: Image digest is required.", nameof(imageDigest));
        }

        string trimmed = imageDigest.Trim().ToLowerInvariant();
        if (!trimmed.StartsWith("sha256:", StringComparison.Ordinal) || trimmed.Length != 71)
        {
            throw new ArgumentException("invalid_digest: Compatibility profile must be keyed by a sha256:<64-hex> image digest, never by tag or name.", nameof(imageDigest));
        }

        for (int i = 7; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                throw new ArgumentException("invalid_digest: Image digest contains non-hex characters.", nameof(imageDigest));
            }
        }

        return trimmed;
    }

    public static string[] ValidateRelaxations(IReadOnlyList<string>? relaxations)
    {
        if (relaxations is null || relaxations.Count == 0)
        {
            throw new ArgumentException("invalid_relaxations: At least one compatibility relaxation must be specified.", nameof(relaxations));
        }

        var distinct = new List<string>(relaxations.Count);
        foreach (string item in relaxations)
        {
            if (string.IsNullOrWhiteSpace(item) || !allowedRelaxations.Contains(item))
            {
                throw new ArgumentException($"invalid_relaxation: Relaxation '{item}' is not permitted.", nameof(relaxations));
            }

            if (!distinct.Contains(item, StringComparer.Ordinal))
            {
                distinct.Add(item);
            }
        }

        return [.. distinct];
    }

    private static void EnsureAdmin(NetworkAccessActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new ArgumentException("Actor userId is required.", nameof(actor));
        }

        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("PermissionDenied: Image compatibility mode is restricted to administrators.");
        }
    }

    private ImageCompatState Load()
    {
        if (state is not null)
        {
            return state;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return state = new ImageCompatState(1, [], []);
        }
        catch (DirectoryNotFoundException)
        {
            return state = new ImageCompatState(1, [], []);
        }

        ImageCompatState loaded = JsonSerializer.Deserialize(bytes, NetworkAccessJsonContext.Default.ImageCompatState)
            ?? throw new InvalidDataException("Image compatibility state file is empty.");
        if (loaded.SchemaVersion != 1 || loaded.Profiles is null || loaded.PendingRequests is null)
        {
            throw new InvalidDataException("Unsupported image compatibility state file.");
        }

        foreach (ImageCompatProfile profile in loaded.Profiles)
        {
            _ = ValidateAndNormalizeDigest(profile.ImageDigest);
            _ = ValidateRelaxations(profile.Relaxations);
        }

        return state = loaded;
    }

    private void Save(ImageCompatState next)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string tempPath = Path.Combine(directory, ".image-compat-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(stream, next, NetworkAccessJsonContext.Default.ImageCompatState);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            state = next;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
