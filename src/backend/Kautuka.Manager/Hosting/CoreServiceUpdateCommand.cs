using System.Text.Json;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.Manager.Hosting;

/// <summary>Fixed local lifecycle command for the root-owned update transaction.</summary>
internal static class CoreServiceUpdateCommand
{
    private const string recoveryRoot = "/var/lib/kautuka-recovery";

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Core update commands require the Linux appliance.");
            if (args.Length == 1 && args[0] == "--update-core-capabilities")
            {
                await Console.Out.WriteLineAsync("core-service-update-v1").ConfigureAwait(false);
                return 0;
            }
            if (args.Length != 2 || args[0] is not ("--update-core-stop" or "--update-core-verify") || !IsOperationId(args[1]))
                throw new InvalidOperationException("Expected a fixed core update action followed by the active operation ID.");
            bool verification = args[0] == "--update-core-verify";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            string journal = await ReadRecordAsync("operation.json", timeout.Token).ConfigureAwait(false);
            string marker = await ReadRecordAsync("maintenance.json", timeout.Token).ConfigureAwait(false);
            string executable = new FileInfo("/proc/self/exe").LinkTarget
                ?? throw new InvalidOperationException("Cannot identify the installed Manager executable.");
            string release = ValidateOperation(args[1], journal, marker, executable, verification);

            // Stop needs neither release images nor configuration/certificate
            // bootstrap. The root helper supplies the runtime owner's clean
            // Podman environment and keeps the long-lived Manager stopped.
            using var configuration = new ConfigurationManager();
            var coreOptions = new CoreServiceOptions();
            var egressOptions = new ContainerEgressOptions();
            RuntimeImageAllowlist inventory = RuntimeImageAllowlist.Empty;
            if (verification)
            {
                configuration.SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false)
                    .AddJsonFile("appsettings.Production.json", optional: true);
                // No environment/command-line configuration providers. Paths
                // and identity stay those of the fixed production appliance.
                configuration["Kautuka:Mode"] = "prod";
                configuration["Kautuka:ContainerStateRoot"] = "/var/lib/kautuka";
                configuration.GetSection(CoreServiceOptions.Section).Bind(coreOptions);
                configuration.GetSection(ContainerEgressOptions.Section).Bind(egressOptions);
                inventory = RuntimeImageAllowlistLoader.Load(Path.Combine(AppContext.BaseDirectory, RuntimeImageAllowlistLoader.FileName),
                    requirePresent: true, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            }
            var runner = new PodmanCommandRunner();
            var archives = new PodmanImageArchiveStore(runner, "/var/lib/kautuka/oci-images", inventory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PodmanImageArchiveStore>.Instance);
            using var egress = new ContainerEgressRuntime(runner, egressOptions, inventory, "/var/lib/kautuka/manager", archives);
            using var core = new CoreServiceSupervisor(runner, egress, inventory,
                new CoreServicePaths("/var/lib/kautuka", KautukaHostNetworkPolicy.ResolveSocketRoot(configuration),
                    coreOptions.WorkspaceRoot ?? "/var/lib/kautuka/workspaces", coreOptions.ConfigurationRoot ?? "/var/lib/kautuka/manager/core-config",
                    KautukaDatabaseWriterLease.LockPath, recoveryRoot), coreOptions, archives);
            IReadOnlyList<CoreServiceUpdateIdentity> cohort = [];
            if (verification) cohort = await core.VerifyForUpdateAsync(timeout.Token).ConfigureAwait(false);
            else await core.StopForUpdateAsync(timeout.Token).ConfigureAwait(false);
            using var output = new Utf8JsonWriter(Console.OpenStandardOutput());
            output.WriteStartObject();
            output.WriteString("action", verification ? "verify" : "stop");
            output.WriteString("operationId", args[1]);
            output.WriteString("release", release);
            output.WriteBoolean(verification ? "ready" : "stopped", true);
            if (verification)
            {
                output.WriteStartArray("cohort");
                foreach (CoreServiceUpdateIdentity service in cohort)
                {
                    output.WriteStartObject();
                    output.WriteString("service", service.Service);
                    output.WriteString("containerId", service.ContainerId);
                    output.WriteString("imageId", service.ImageId);
                    output.WriteEndObject();
                }
                output.WriteEndArray();
            }
            output.WriteEndObject();
            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException
            or JsonException or OperationCanceledException or PlatformNotSupportedException)
        {
            await Console.Error.WriteLineAsync("Core update operation refused: " + error.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<string> ReadRecordAsync(string name, CancellationToken ct)
    {
        // Ownership and non-writable ancestry are enforced by the root helper
        // before launching this same-owner command. Never accept caller paths.
        if (new DirectoryInfo(recoveryRoot).LinkTarget is not null)
            throw new InvalidOperationException("The recovery directory cannot be a symbolic link.");
        var file = new FileInfo(Path.Combine(recoveryRoot, name));
        if (file.LinkTarget is not null || !file.Exists || file.Length is < 1 or > 65536)
            throw new InvalidOperationException("The active update record is missing or invalid.");
        string text = await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false);
        if (text.Length > 65536) throw new InvalidOperationException("The active update record exceeds its limit.");
        return text;
    }

    internal static string ValidateOperation(string operationId, string journal, string marker, string executable, bool verification = false)
    {
        if (!IsOperationId(operationId)) throw new InvalidOperationException("Invalid update operation ID.");
        using JsonDocument durable = JsonDocument.Parse(journal);
        using JsonDocument fence = JsonDocument.Parse(marker);
        JsonElement record = durable.RootElement, maintenance = fence.RootElement;
        if (record.ValueKind != JsonValueKind.Object || maintenance.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The update records must be objects.");
        foreach (string field in new[] { "format", "operationId", "phase", "sourceRelease", "targetRelease", "dataRoot", "sourcePointers" })
            if (!record.TryGetProperty(field, out JsonElement value) || !maintenance.TryGetProperty(field, out JsonElement other)
                || !JsonElement.DeepEquals(value, other))
                throw new InvalidOperationException("The active update records disagree.");
        if (!record.GetProperty("format").TryGetInt32(out int format) || format != 1
            || String(record, "operationId") != operationId || String(record, "dataRoot") != "/var/lib/kautuka"
            || String(record, "phase") is not ("capturing" or "verified" or "verifying" or "starting" or "committed" or "restoring" or "recovered" or "completed" or "aborted"))
            throw new InvalidOperationException("The active update identity or phase is invalid.");
        string source = String(record.GetProperty("sourcePointers"), "manager");
        string target = String(record, "targetRelease");
        if (!IsRelease(source) || !IsRelease(target) || String(record, "sourceRelease") != source)
            throw new InvalidOperationException("The recorded Manager releases are invalid.");
        if (verification)
        {
            string expected = String(record, "phase") switch
            {
                "verified" or "verifying" or "starting" or "committed" => target,
                "recovered" or "completed" or "aborted" => source,
                _ => throw new InvalidOperationException("Core verification is not permitted in the current phase."),
            };
            if (executable != "/opt/kautuka/manager/versions/" + expected + "/kautuka-manager")
                throw new InvalidOperationException("The verification executable does not match the selected update phase.");
            return expected;
        }
        foreach (string release in new[] { source, target })
            if (executable == "/opt/kautuka/manager/versions/" + release + "/kautuka-manager") return release;
        throw new InvalidOperationException("The Manager executable is outside the recorded source/target releases.");
    }

    private static string String(JsonElement document, string name)
        => document.ValueKind == JsonValueKind.Object && document.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidOperationException("The update record is missing a required string.");

    private static bool IsOperationId(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigitLower);
    private static bool IsRelease(string value) => value.Length is > 0 and <= 128 && value[0] != '.'
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}
