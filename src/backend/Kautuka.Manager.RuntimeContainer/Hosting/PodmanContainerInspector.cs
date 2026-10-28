using System.Text.Json;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed class ContainerInspectionUnavailableException : IOException
{
    public ContainerInspectionUnavailableException()
    {
    }

    public ContainerInspectionUnavailableException(string message) : base(message)
    {
    }

    public ContainerInspectionUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

internal static class PodmanContainerInspector
{
    internal static async Task<string?> ReadAsync(PodmanCommandRunner runner, string name, CancellationToken cancellationToken)
    {
        PodmanCommandResult inspection = await runner.RunAsync(["inspect", name], 10, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (inspection.ExitCode != 0)
        {
            if (inspection.TerminationReason != ProcessTerminationReason.TimedOut)
            {
                PodmanCommandResult existence = await runner.RunAsync(["container", "exists", name], 10, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (existence.ExitCode == 1 && existence.TerminationReason == ProcessTerminationReason.NonZeroExit)
                {
                    return null;
                }
            }

            throw new ContainerInspectionUnavailableException($"Container '{name}' could not be inspected. Container changes are deferred.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(inspection.StdOut);
            JsonElement container = Container(document.RootElement);
            if (!container.TryGetProperty("HostConfig", out JsonElement host) || host.ValueKind != JsonValueKind.Object
                || !container.TryGetProperty("Config", out JsonElement config) || config.ValueKind != JsonValueKind.Object
                || !container.TryGetProperty("State", out JsonElement state) || state.ValueKind != JsonValueKind.Object
                || !state.TryGetProperty("Status", out JsonElement status) || status.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(status.GetString()))
            {
                throw new JsonException("Container inspection is incomplete.");
            }
        }
        catch (JsonException ex)
        {
            throw new ContainerInspectionUnavailableException($"Container '{name}' returned an invalid inspection. Container changes are deferred.", ex);
        }

        return inspection.StdOut;
    }

    internal static JsonElement Container(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
        {
            root = root[0];
        }

        return root.ValueKind == JsonValueKind.Object ? root : throw new JsonException("Expected one container inspection.");
    }
}
