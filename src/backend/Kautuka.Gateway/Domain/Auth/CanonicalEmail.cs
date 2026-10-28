namespace Kautuka.Gateway.Domain.Auth;

internal static class CanonicalEmail
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = FoldAscii(value.Trim());
        int at = text.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != text.LastIndexOf('@') || at == text.Length - 1)
        {
            return null;
        }

        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                return null;
            }
        }

        return text;
    }

    private static string FoldAscii(string value)
    {
        var chars = value.ToCharArray();
        for (int index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'A' and <= 'Z')
            {
                chars[index] = (char)(chars[index] + 32);
            }
        }

        return new string(chars);
    }
}
