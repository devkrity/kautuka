using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Kautuka.NetworkAccessController;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

/// <summary>
/// Host-side settings needed for the Windows development path, where Podman runs inside
/// Podman Machine and Envoy reaches Manager over a dedicated mTLS TCP listener.
/// </summary>
public sealed record WindowsPodmanDevEgressOptions(
    IPAddress DecisionEndpointAddress,
    string EnvoyCertificateDirectory,
    string PodmanMachineName,
    string RemoteHookStateDirectory,
    X509Certificate2 CaCertificate,
    X509Certificate2 ServerCertificate)
{
    public const string DecisionHostName = EnvoyMtlsCertificateAuthority.DefaultServerDnsSan;

    public static async Task<WindowsPodmanDevEgressOptions> CreateAsync(
        PodmanCommandRunner podmanRunner,
        string certificateDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(podmanRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateDirectory);

        PodmanCommandResult route = await podmanRunner.RunAsync(
            ["machine", "ssh", "ip route show default"],
            10,
            cancellationToken).ConfigureAwait(false);
        IPAddress decisionAddress = ParsePodmanMachineHostGateway(route);
        if (!IsLocalAddress(decisionAddress))
        {
            throw new InvalidOperationException(
                $"Podman Machine host gateway '{decisionAddress}' is not assigned to a local interface; refusing to bind the NAC listener.");
        }

        (X509Certificate2 caCertificate,
            X509Certificate2 serverCertificate,
            X509Certificate2 clientCertificate)
            = EnvoyMtlsCertificateAuthority.CreateDedicatedBundle();

        string resolvedCertificateDirectory = Path.GetFullPath(certificateDirectory);
        Directory.CreateDirectory(resolvedCertificateDirectory);
        await WriteClientBundleAsync(
            resolvedCertificateDirectory,
            caCertificate,
            clientCertificate,
            cancellationToken).ConfigureAwait(false);

        (string machineName, string machineHome) = await ResolveDefaultMachineAsync(podmanRunner, cancellationToken).ConfigureAwait(false);
        string hookSourceDirectory = FindRepositoryNacDirectory();
        string remoteHookDirectory = $"{machineHome}/.local/share/kautuka/nac/hooks.d";
        string remoteHookStateDirectory = $"{machineHome}/.local/state/kautuka/nac";
        string remoteConfigDirectory = $"{machineHome}/.config/containers/containers.conf.d";
        string remoteHookScriptDirectory = $"{machineHome}/.local/lib/kautuka/nac";

        await EnsureRemoteDirectoryAsync(
            podmanRunner,
            machineName,
            [remoteHookDirectory, remoteHookStateDirectory, remoteConfigDirectory, remoteHookScriptDirectory],
            cancellationToken).ConfigureAwait(false);

        string stagedHookConfig = Path.Combine(resolvedCertificateDirectory, "kautuka_hook.json");
        string remoteHookScript = $"{remoteHookScriptDirectory}/kautuka_hook.py";
        string remoteHookTemplate = $"{remoteHookScriptDirectory}/kautuka_workload.nft.json.tmpl";
        await File.WriteAllTextAsync(
            stagedHookConfig,
            $$"""
            {
              "version": "1.0.0",
              "hook": { "path": "{{remoteHookScript}}" },
              "when": { "always": true },
              "stages": ["createRuntime"]
            }
            """,
            cancellationToken).ConfigureAwait(false);

        await CopyToMachineAsync(podmanRunner, machineName,
            Path.Combine(hookSourceDirectory, "kautuka_hook.py"), remoteHookScript, cancellationToken).ConfigureAwait(false);
        await CopyToMachineAsync(podmanRunner, machineName,
            Path.Combine(hookSourceDirectory, "kautuka_workload.nft.json.tmpl"), remoteHookTemplate, cancellationToken).ConfigureAwait(false);
        await CopyToMachineAsync(podmanRunner, machineName,
            stagedHookConfig, $"{remoteHookDirectory}/kautuka_hook.json", cancellationToken).ConfigureAwait(false);

        string remoteConfigFragment = Path.Combine(resolvedCertificateDirectory, "99-kautuka-nac.conf");
        // containers/common otherwise replaces hooks_dir and hides user hooks. Append mode
        // persists for subsequently loaded fragments, so the NAC drop-in is named late and
        // later hooks_dir assignments will append instead of replacing the accumulated list.
        await File.WriteAllTextAsync(remoteConfigFragment,
            RenderHookDirectoryConfig(remoteHookDirectory),
            cancellationToken).ConfigureAwait(false);
        await CopyToMachineAsync(podmanRunner, machineName,
            remoteConfigFragment, $"{remoteConfigDirectory}/99-kautuka-nac.conf", cancellationToken).ConfigureAwait(false);

        PodmanCommandResult permissions = await podmanRunner.RunAsync(
            ["machine", "ssh", $"chmod 700 '{remoteHookStateDirectory}'; chmod 700 '{remoteHookScriptDirectory}'; chmod 700 '{remoteHookDirectory}'; chmod 755 '{remoteHookScript}'"],
            10,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(permissions, "setting up the Podman Machine NAC hook permissions");

        return new WindowsPodmanDevEgressOptions(
            decisionAddress,
            resolvedCertificateDirectory,
            machineName,
            remoteHookStateDirectory,
            caCertificate,
            serverCertificate);
    }

    private static async Task<(string Name, string Home)> ResolveDefaultMachineAsync(
        PodmanCommandRunner podmanRunner,
        CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await podmanRunner.RunAsync(
            ["machine", "list", "--format", "json"],
            10,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "discovering the default Podman Machine");
        using JsonDocument document = JsonDocument.Parse(result.StdOut);
        JsonElement machines = document.RootElement;
        JsonElement machine = machines.ValueKind == JsonValueKind.Array
            ? machines.EnumerateArray().FirstOrDefault(item => item.TryGetProperty("Default", out JsonElement isDefault) && isDefault.GetBoolean())
            : default;
        if (machine.ValueKind != JsonValueKind.Object
            || !machine.TryGetProperty("Name", out JsonElement name)
            || !machine.TryGetProperty("RemoteUsername", out JsonElement username)
            || string.IsNullOrWhiteSpace(name.GetString())
            || string.IsNullOrWhiteSpace(username.GetString()))
        {
            throw new InvalidOperationException("Could not identify the default Podman Machine user for NAC hook setup.");
        }

        string machineName = name.GetString()!;
        string home = $"/home/{username.GetString()}";
        return (machineName, home);
    }

    private static async Task EnsureRemoteDirectoryAsync(
        PodmanCommandRunner podmanRunner,
        string machineName,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken)
    {
        string quotedDirectories = string.Join(' ', directories.Select(static directory => $"'{directory}'"));
        PodmanCommandResult result = await podmanRunner.RunAsync(
            ["machine", "ssh", $"mkdir -p -- {quotedDirectories}"],
            10,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, $"creating the Podman Machine NAC directories for '{machineName}'");
    }

    private static async Task CopyToMachineAsync(
        PodmanCommandRunner podmanRunner,
        string machineName,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await podmanRunner.RunAsync(
            ["machine", "cp", sourcePath, $"{machineName}:{destinationPath}"],
            20,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, $"copying NAC hook file '{Path.GetFileName(sourcePath)}' into Podman Machine");
    }

    private static string FindRepositoryNacDirectory()
    {
        string? fromCurrentDirectory = FindRepositoryNacDirectoryFrom(Directory.GetCurrentDirectory());
        string? fromBaseDirectory = FindRepositoryNacDirectoryFrom(AppContext.BaseDirectory);
        return fromCurrentDirectory ?? fromBaseDirectory
            ?? throw new InvalidOperationException("The Windows development NAC hook sources were not found in the repository checkout.");
    }

    private static string? FindRepositoryNacDirectoryFrom(string start)
    {
        DirectoryInfo? current = new(Path.GetFullPath(start));
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "scripts", "nac");
            if (File.Exists(Path.Combine(candidate, "kautuka_hook.py"))
                && File.Exists(Path.Combine(candidate, "kautuka_workload.nft.json.tmpl")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static void EnsureSuccess(PodmanCommandResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed {operation} (rc {result.ExitCode}): {result.StdErr}");
        }
    }

    public static IPAddress ParsePodmanMachineHostGateway(PodmanCommandResult routeResult)
    {
        ArgumentNullException.ThrowIfNull(routeResult);
        if (routeResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not discover the Podman Machine host gateway (rc {routeResult.ExitCode}): {routeResult.StdErr}");
        }

        foreach (string line in routeResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 3
                || !string.Equals(fields[0], "default", StringComparison.Ordinal)
                || !string.Equals(fields[1], "via", StringComparison.Ordinal)
                || !IPAddress.TryParse(fields[2], out IPAddress? address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || IPAddress.IsLoopback(address))
            {
                continue;
            }

            return address;
        }

        throw new InvalidOperationException("Podman Machine did not report a usable IPv4 default gateway.");
    }

    internal static string RenderHookDirectoryConfig(string remoteHookDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteHookDirectory);
        string escapedDirectory = remoteHookDirectory
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return $"[engine]{Environment.NewLine}hooks_dir = [\"{escapedDirectory}\", {{append = true}}]{Environment.NewLine}";
    }

    public static async Task WriteClientBundleAsync(
        string certificateDirectory,
        X509Certificate2 caCertificate,
        X509Certificate2 clientCertificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateDirectory);
        ArgumentNullException.ThrowIfNull(caCertificate);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        using ECDsa? privateKey = clientCertificate.GetECDsaPrivateKey();
        if (privateKey is null)
        {
            throw new InvalidOperationException("The generated Envoy client certificate does not have an ECDSA private key.");
        }

        string fullDirectory = Path.GetFullPath(certificateDirectory);
        Directory.CreateDirectory(fullDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(fullDirectory, "ca.crt"),
            caCertificate.ExportCertificatePem(),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(fullDirectory, "client.crt"),
            clientCertificate.ExportCertificatePem(),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(fullDirectory, "client.key"),
            privateKey.ExportPkcs8PrivateKeyPem(),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsLocalAddress(IPAddress address)
        => NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(static networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.Equals(address));
}
