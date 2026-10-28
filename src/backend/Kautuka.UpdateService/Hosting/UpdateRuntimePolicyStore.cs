using System.Text.Json.Serialization;

using Kautuka.UpdateService.State;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

internal interface IUpdateRuntimePolicyStore
{
    ValueTask<UpdateRuntimePolicy> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<UpdateRuntimePolicy> SetReleaseChannelAsync(string releaseChannel, CancellationToken cancellationToken = default);
}

internal sealed record UpdateRuntimePolicy(
    int SchemaVersion,
    string ReleaseChannel)
{
    public static UpdateRuntimePolicy CreateDefault(string releaseChannel)
        => new(1, NormalizeReleaseChannel(releaseChannel));

    public static string NormalizeReleaseChannel(string? candidate)
        => string.Equals(candidate?.Trim(), "stable", StringComparison.OrdinalIgnoreCase)
            ? "stable"
            : string.Equals(candidate?.Trim(), "preview", StringComparison.OrdinalIgnoreCase)
                ? "preview"
                : throw new InvalidOperationException($"Unsupported release channel '{candidate ?? string.Empty}'. Expected 'stable' or 'preview'.");
}

internal sealed class UpdateRuntimePolicyStore(IOptions<UpdateOptions> options) : IUpdateRuntimePolicyStore
{
    private readonly string policyFilePath = Path.Combine(options.Value.StateRootPath, "update-runtime-policy.json");
    private readonly string defaultReleaseChannel = options.Value.ReleaseChannel;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask<UpdateRuntimePolicy> GetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(policyFilePath)!);

            UpdateRuntimePolicy? policy = await TryReadAsync(policyFilePath, cancellationToken).ConfigureAwait(false);
            if (policy is not null)
            {
                return policy with { ReleaseChannel = UpdateRuntimePolicy.NormalizeReleaseChannel(policy.ReleaseChannel) };
            }

            if (File.Exists(policyFilePath))
            {
                throw new InvalidDataException($"State file '{policyFilePath}' contains a JSON null value.");
            }

            return UpdateRuntimePolicy.CreateDefault(defaultReleaseChannel);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<UpdateRuntimePolicy> SetReleaseChannelAsync(string releaseChannel, CancellationToken cancellationToken = default)
    {
        UpdateRuntimePolicy policy = new(1, UpdateRuntimePolicy.NormalizeReleaseChannel(releaseChannel));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WritePolicyAsync(policy, cancellationToken).ConfigureAwait(false);
            return policy;
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<UpdateRuntimePolicy?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await AtomicJsonStateFile.ReadAsync(
            path,
            UpdateRuntimePolicyJsonContext.Default.UpdateRuntimePolicy,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WritePolicyAsync(UpdateRuntimePolicy policy, CancellationToken cancellationToken)
    {
        await WritePolicyFileAsync(policyFilePath, policy, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WritePolicyFileAsync(string path, UpdateRuntimePolicy policy, CancellationToken cancellationToken)
    {
        await AtomicJsonStateFile.WriteAsync(
            path,
            policy,
            UpdateRuntimePolicyJsonContext.Default.UpdateRuntimePolicy,
            cancellationToken).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(UpdateRuntimePolicy))]
internal sealed partial class UpdateRuntimePolicyJsonContext : JsonSerializerContext
{
}
