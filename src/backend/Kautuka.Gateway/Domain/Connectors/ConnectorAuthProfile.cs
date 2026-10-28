using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kautuka.Gateway.Domain.Connectors;

internal sealed class ConnectorAuthProfile
{
    public const string UnconfiguredClientId = "00000000-0000-0000-0000-000000000000";

    public const string NotConfiguredMessage =
        "Microsoft sign-in is not configured on this appliance yet. The Entra application id has not been installed with this connector.";

    public const string SignInRequiredMessage =
        "This connector is not signed in. Use the sign-in button on the Connectors page.";

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Grant { get; init; }

    public required string AuthorizeUrl { get; init; }

    public required string TokenUrl { get; init; }

    public required string Scopes { get; init; }

    public required string RedirectUri { get; init; }

    public required string StartUrl { get; init; }

    public required string RefreshCredentialKey { get; init; }

    public required string AccessTokenEnvironmentKey { get; init; }

    public required string RevokeUrl { get; init; }

    public required string SignInLabel { get; init; }

    public required string EntryPoint { get; init; }

    public required string[] ToolNames { get; init; }

    public required string[] AskFirst { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public bool RequiresClientSecret { get; init; }

    public string? Prompt { get; init; }

    public string? AccessType { get; init; }

    public bool UseDpop { get; init; }

    public string? DpopKeyCredentialKey { get; init; }

    public string? DpopNonceCredentialKey { get; init; }

    public string ConfigurationMessage { get; init; } = NotConfiguredMessage;

    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(ClientId)
            && (!RequiresClientSecret || !string.IsNullOrWhiteSpace(ClientSecret));

    public bool IsReady(ConnectorAuthEndpoint endpoint)
    {
        if (endpoint.IsMisconfigured)
        {
            return false;
        }

        if (endpoint.IsHosted)
        {
            return endpoint.BrokerUrl(Id) is not null;
        }

        return IsConfigured;
    }

    public static bool Matches(string? connectorId, string profileId)
    {
        if (string.IsNullOrWhiteSpace(connectorId) || string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        if (string.Equals(connectorId, profileId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return connectorId.EndsWith("::" + profileId, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ReadClientId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (string.Equals(line, UnconfiguredClientId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, "not-configured", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Guid.TryParse(line, out Guid _))
            {
                return line;
            }

            return IsGoogleClientId(line) ? line : null;
        }

        return null;
    }

    public static string? ReadClientSecret(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (string.Equals(line, "not-configured", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, UnconfiguredClientId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (line.Length is < 16 or > 200)
            {
                return null;
            }

            foreach (char character in line)
            {
                if (character is < '!' or > '~')
                {
                    return null;
                }
            }

            return line;
        }

        return null;
    }

    private static bool IsGoogleClientId(string line)
    {
        const string suffix = ".apps.googleusercontent.com";
        if (!line.EndsWith(suffix, StringComparison.Ordinal) || line.Length <= suffix.Length + 8)
        {
            return false;
        }

        string prefix = line[..^suffix.Length];
        foreach (char character in prefix)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    internal static string? CredentialKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80)
        {
            return null;
        }

        foreach (char character in value)
        {
            if (character is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
            {
                return null;
            }
        }

        return value;
    }

    internal static string? QueryToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40)
        {
            return null;
        }

        foreach (char character in value)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_'))
            {
                return null;
            }
        }

        return value;
    }

    public static bool TryNormalizeReturnOrigin(string? origin, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (uri.AbsolutePath is not "/" and not "")
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        bool https = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        bool http = string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase);
        if (!https && !http)
        {
            return false;
        }

        if (http && !IsPrivateHost(uri.IdnHost))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    public static string CreateCodeVerifier()
        => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string CreateCodeChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState()
        => Base64Url(RandomNumberGenerator.GetBytes(32));

    public string BuildAuthorizeUrl(string state, string codeChallenge, string redirectUri)
    {
        var query = new StringBuilder();
        Append(query, "client_id", ClientId ?? string.Empty);
        Append(query, "response_type", "code");
        Append(query, "redirect_uri", redirectUri);
        Append(query, "scope", Scopes);
        Append(query, "state", state);
        Append(query, "code_challenge", codeChallenge);
        Append(query, "code_challenge_method", "S256");
        Append(query, "prompt", string.IsNullOrWhiteSpace(Prompt) ? "select_account" : Prompt);
        if (!string.IsNullOrWhiteSpace(AccessType))
        {
            Append(query, "access_type", AccessType);
        }
        string separator = AuthorizeUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return AuthorizeUrl + separator + query;
    }

    public string BuildHostedStartUrl(string host, string returnOrigin, string state, string codeChallenge)
    {
        string hash = "return=" + Uri.EscapeDataString(returnOrigin + "/connectors")
            + "&connector=" + Uri.EscapeDataString(Id)
            + "&state=" + Uri.EscapeDataString(state)
            + "&challenge=" + Uri.EscapeDataString(codeChallenge);
        return host + "/start#" + hash;
    }

    private static void Append(StringBuilder query, string name, string value)
    {
        if (query.Length > 0)
        {
            _ = query.Append('&');
        }

        _ = query.Append(Uri.EscapeDataString(name));
        _ = query.Append('=');
        _ = query.Append(Uri.EscapeDataString(value));
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsPrivateHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal))
        {
            return true;
        }

        if (Regex.IsMatch(
                host,
                @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.local$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out IPAddress? address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
        {
            if (bytes[0] == 10)
            {
                return true;
            }

            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && bytes.Length > 0)
        {
            return (bytes[0] & 0xfe) == 0xfc;
        }

        return false;
    }
}

internal sealed class ConnectorAuthCatalog
{
    private readonly ConnectorAuthProfile[] profiles;

    public ConnectorAuthCatalog(IEnumerable<ConnectorAuthProfile> profiles)
    {
        this.profiles = [.. profiles];
    }

    public IReadOnlyList<ConnectorAuthProfile> Profiles => profiles;

    public ConnectorAuthProfile? Find(string? connectorId)
    {
        foreach (ConnectorAuthProfile profile in profiles)
        {
            if (ConnectorAuthProfile.Matches(connectorId, profile.Id))
            {
                return profile;
            }
        }

        return null;
    }

    public static ConnectorAuthCatalog Load(string contentRoot)
    {
        var profiles = new List<ConnectorAuthProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in CandidateDirectories(contentRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string descriptorPath in Directory.EnumerateFiles(directory, "connector.json", SearchOption.AllDirectories))
            {
                ConnectorAuthProfile? profile = TryRead(descriptorPath);
                if (profile is null || !seen.Add(profile.Id))
                {
                    continue;
                }

                profiles.Add(profile);
            }
        }

        return new ConnectorAuthCatalog(profiles);
    }

    public static ConnectorAuthProfile? TryRead(string descriptorPath)
    {
        if (!File.Exists(descriptorPath))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptorPath));
        JsonElement root = document.RootElement;
        if (!TryGetString(root, "id", out string id)
            || !TryGetString(root, "name", out string name)
            || !root.TryGetProperty("auth", out JsonElement auth)
            || !TryGetString(auth, "grant", out string grant)
            || !string.Equals(grant, "authorization_code", StringComparison.Ordinal))
        {
            return null;
        }

        if (!TryGetString(auth, "authorizeUrl", out string authorizeUrl)
            || !TryGetString(auth, "tokenUrl", out string tokenUrl)
            || !TryGetString(auth, "scopes", out string scopes)
            || !TryGetString(auth, "redirectUri", out string redirectUri)
            || !TryGetString(auth, "startUrl", out string startUrl)
            || !TryGetString(auth, "refreshCredentialKey", out string refreshKey)
            || !TryGetString(auth, "accessTokenEnvironmentKey", out string accessKey)
            || !TryGetString(auth, "revokeUrl", out string revokeUrl)
            || !TryGetString(auth, "signInLabel", out string signInLabel))
        {
            return null;
        }

        string entryPoint = "connector";
        if (root.TryGetProperty("server", out JsonElement server)
            && TryGetString(server, "entryPoint", out string configuredEntry))
        {
            entryPoint = configuredEntry;
        }

        string directory = Path.GetDirectoryName(descriptorPath) ?? string.Empty;
        string clientIdFile = TryGetString(auth, "clientIdFile", out string configuredFile)
            ? configuredFile
            : "client-id.txt";
        string clientIdPath = Path.Combine(directory, clientIdFile);
        string? clientId = File.Exists(clientIdPath)
            ? ConnectorAuthProfile.ReadClientId(File.ReadAllText(clientIdPath))
            : null;
        bool requiresSecret = TryGetString(auth, "clientSecretFile", out string secretFile);
        string? clientSecret = null;
        if (requiresSecret)
        {
            string secretPath = Path.Combine(directory, secretFile);
            clientSecret = File.Exists(secretPath)
                ? ConnectorAuthProfile.ReadClientSecret(File.ReadAllText(secretPath))
                : null;
        }

        return new ConnectorAuthProfile
        {
            Id = id,
            Name = name,
            Description = TryGetString(root, "description", out string description) ? description : string.Empty,
            Grant = grant,
            AuthorizeUrl = authorizeUrl,
            TokenUrl = tokenUrl,
            Scopes = scopes,
            RedirectUri = redirectUri,
            StartUrl = startUrl,
            RefreshCredentialKey = refreshKey,
            AccessTokenEnvironmentKey = accessKey,
            RevokeUrl = revokeUrl,
            SignInLabel = signInLabel,
            EntryPoint = entryPoint,
            ToolNames = ReadStringArray(root, "tools"),
            AskFirst = ReadStringArray(root, "askFirst"),
            ClientId = clientId,
            ClientSecret = clientSecret,
            RequiresClientSecret = requiresSecret,
            UseDpop = auth.TryGetProperty("dpop", out JsonElement dpop) && dpop.ValueKind == JsonValueKind.True,
            DpopKeyCredentialKey = ConnectorAuthProfile.CredentialKey(TryGetString(auth, "dpopKeyCredentialKey", out string dpopKey) ? dpopKey : null),
            DpopNonceCredentialKey = ConnectorAuthProfile.CredentialKey(TryGetString(auth, "dpopNonceCredentialKey", out string dpopNonce) ? dpopNonce : null),
            Prompt = ConnectorAuthProfile.QueryToken(TryGetString(auth, "prompt", out string prompt) ? prompt : null),
            AccessType = ConnectorAuthProfile.QueryToken(TryGetString(auth, "accessType", out string accessType) ? accessType : null),
            ConfigurationMessage = TryGetString(auth, "notConfiguredMessage", out string configurationMessage)
                ? configurationMessage
                : ConnectorAuthProfile.NotConfiguredMessage,
        };
    }

    private static IEnumerable<string> CandidateDirectories(string contentRoot)
    {
        string? repository = FindRepositoryConnectors(contentRoot);
        if (repository is not null)
        {
            yield return repository;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "connectors");
        if (!string.IsNullOrWhiteSpace(contentRoot))
        {
            yield return Path.Combine(contentRoot, "connectors");
        }
    }

    private static string? FindRepositoryConnectors(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        DirectoryInfo? directory = new(startPath);
        if (!directory.Exists)
        {
            directory = directory.Parent;
        }

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "backend", "connectors");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(Path.Combine(directory.FullName, "src", "Kautuka.slnx")))
            {
                return Directory.Exists(candidate) ? candidate : null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string[] ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                items.Add(item.GetString()!.Trim());
            }
        }

        return [.. items];
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text.Trim();
        return true;
    }
}
