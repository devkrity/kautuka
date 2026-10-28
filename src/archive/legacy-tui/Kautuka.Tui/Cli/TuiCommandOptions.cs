// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Cli/TuiCommandOptions.cs | Namespace: Kautuka.Tui.Cli
using System.Text.Json;

namespace Kautuka.Tui.Cli;

public sealed record TuiCommandOptions(
    Uri GatewayUrl,
    string? AccessToken,
    string? Username,
    string? Password,
    IReadOnlyList<string> Positionals)
{
    private const int defaultGatewayPort = 20000;
    private const string defaultGatewayHost = "127.0.0.1";

    public static Uri ResolveGatewayUrl()
        => new($"http://{defaultGatewayHost}:{ResolveGatewayPort()}", UriKind.Absolute);

    public static int ResolveGatewayPort()
        => TryReadGatewayPortFromConfig(out int configPort)
            ? configPort
            : defaultGatewayPort;

    public static TuiCommandOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<string> positionals = [];
        string? accessToken = null;
        string? username = null;
        string? password = null;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            if (index == args.Length - 1)
            {
                throw new ArgumentException($"Option '{arg}' requires a value.");
            }

            string value = args[++index];
            switch (arg)
            {
                case "--token":
                    accessToken = value;
                    break;
                case "--username":
                    username = value;
                    break;
                case "--password":
                    password = value;
                    break;
                default:
                    positionals.Add(arg);
                    positionals.Add(value);
                    break;
            }
        }

        return new TuiCommandOptions(ResolveGatewayUrl(), accessToken, username, password, [.. positionals]);
    }

    private static bool TryReadGatewayPortFromConfig(out int port)
    {
        foreach (string configPath in EnumerateConfigPaths())
        {
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(configPath);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("services", out JsonElement services)
                    && services.TryGetProperty("gateway", out JsonElement gateway)
                    && gateway.TryGetProperty("port", out JsonElement portElement)
                    && portElement.ValueKind == JsonValueKind.Number
                    && portElement.TryGetInt32(out int parsedPort)
                    && parsedPort is > 0 and <= 65535)
                {
                    port = parsedPort;
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (JsonException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        port = 0;
        return false;
    }

    private static IEnumerable<string> EnumerateConfigPaths()
    {
        string? currentDirectory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            yield return Path.Combine(currentDirectory, "config.json");

            DirectoryInfo? parent = Directory.GetParent(currentDirectory);
            if (parent is null)
            {
                yield break;
            }

            currentDirectory = parent.FullName;
        }
    }
}
