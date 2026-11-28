namespace Kautuka.Gateway.Domain.Connectors;

internal static class ConnectorEnvironmentKeyPolicy
{
    private static readonly HashSet<string> proxySettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
    };

    private static readonly HashSet<string> managedSettingNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HOME",
        "NPM_CONFIG_REGISTRY",
        "NPM_CONFIG_USERCONFIG",
        "NPM_CONFIG_GLOBALCONFIG",
        "NPM_CONFIG_CACHE",
        "UV_INDEX_URL",
        "UV_DEFAULT_INDEX",
        "UV_EXTRA_INDEX_URL",
        "UV_INDEX",
        "UV_CONFIG_FILE",
        "UV_CACHE_DIR",
        "PIP_INDEX_URL",
        "PIP_EXTRA_INDEX_URL",
        "PIP_CONFIG_FILE",
        "PIP_TRUSTED_HOST",
        "PIP_CACHE_DIR",
    };

    public static bool IsSupported(string? key, out string message)
    {
        string trimmed = key?.Trim() ?? string.Empty;
        if (!IsPortableIdentifier(trimmed))
        {
            message = $"Setting name '{trimmed}' isn't valid. Use only letters, numbers, and underscores, starting with a letter or underscore.";
            return false;
        }

        if (proxySettingNames.Contains(trimmed))
        {
            message = $"Setting '{trimmed}' can't be used on a connector because Kautuka manages network connections directly.";
            return false;
        }

        if (managedSettingNames.Contains(trimmed))
        {
            message = $"Setting '{trimmed}' is managed by Kautuka and can't be changed on a connector.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    public static bool TryValidate(IEnumerable<string>? keys, out string message)
    {
        if (keys is not null)
        {
            foreach (string key in keys)
            {
                if (!IsSupported(key, out message))
                {
                    return false;
                }
            }
        }

        message = string.Empty;
        return true;
    }

    public static void EnsureSupported(string? key)
    {
        if (!IsSupported(key, out string message))
        {
            throw new ArgumentException(message, nameof(key));
        }
    }

    public static void EnsureSupported(IEnumerable<string>? keys)
    {
        if (!TryValidate(keys, out string message))
        {
            throw new ArgumentException(message, nameof(keys));
        }
    }

    private static bool IsPortableIdentifier(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !(char.IsAsciiLetter(key[0]) || key[0] == '_'))
        {
            return false;
        }

        for (int index = 1; index < key.Length; index++)
        {
            char character = key[index];
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
