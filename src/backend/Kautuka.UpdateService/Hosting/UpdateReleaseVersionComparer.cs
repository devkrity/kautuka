namespace Kautuka.UpdateService.Hosting;

internal static class UpdateReleaseVersionComparer
{
    public static bool TryCompare(string left, string right, out int comparison)
    {
        comparison = 0;

        if (!TryParse(left, out ParsedVersion? parsedLeftNullable) || !TryParse(right, out ParsedVersion? parsedRightNullable))
        {
            return false;
        }

        ParsedVersion parsedLeft = parsedLeftNullable!;
        ParsedVersion parsedRight = parsedRightNullable!;

        int maxCoreLength = Math.Max(parsedLeft.CoreIdentifiers.Length, parsedRight.CoreIdentifiers.Length);
        for (int index = 0; index < maxCoreLength; index++)
        {
            int leftPart = index < parsedLeft.CoreIdentifiers.Length ? parsedLeft.CoreIdentifiers[index] : 0;
            int rightPart = index < parsedRight.CoreIdentifiers.Length ? parsedRight.CoreIdentifiers[index] : 0;
            if (leftPart != rightPart)
            {
                comparison = leftPart.CompareTo(rightPart);
                return true;
            }
        }

        if (parsedLeft.PrereleaseIdentifiers.Length == 0 && parsedRight.PrereleaseIdentifiers.Length == 0)
        {
            comparison = 0;
            return true;
        }

        if (parsedLeft.PrereleaseIdentifiers.Length == 0)
        {
            comparison = 1;
            return true;
        }

        if (parsedRight.PrereleaseIdentifiers.Length == 0)
        {
            comparison = -1;
            return true;
        }

        int prereleaseLength = Math.Max(parsedLeft.PrereleaseIdentifiers.Length, parsedRight.PrereleaseIdentifiers.Length);
        for (int index = 0; index < prereleaseLength; index++)
        {
            if (index >= parsedLeft.PrereleaseIdentifiers.Length)
            {
                comparison = -1;
                return true;
            }

            if (index >= parsedRight.PrereleaseIdentifiers.Length)
            {
                comparison = 1;
                return true;
            }

            comparison = ComparePrereleaseIdentifier(parsedLeft.PrereleaseIdentifiers[index], parsedRight.PrereleaseIdentifiers[index]);
            if (comparison != 0)
            {
                return true;
            }
        }

        comparison = 0;
        return true;
    }

    private static bool TryParse(string value, out ParsedVersion? parsedVersion)
    {
        parsedVersion = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim().TrimStart('v', 'V');
        int buildSeparatorIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (buildSeparatorIndex >= 0)
        {
            normalized = normalized[..buildSeparatorIndex];
        }

        string[] split = normalized.Split('-', 2, StringSplitOptions.TrimEntries);
        string[] coreParts = split[0].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (coreParts.Length == 0)
        {
            return false;
        }

        int[] coreIdentifiers = new int[coreParts.Length];
        for (int index = 0; index < coreParts.Length; index++)
        {
            if (!int.TryParse(coreParts[index], out coreIdentifiers[index]))
            {
                return false;
            }
        }

        string[] prereleaseIdentifiers = split.Length == 2
            ? split[1].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        parsedVersion = new ParsedVersion(coreIdentifiers, prereleaseIdentifiers);
        return true;
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        bool leftNumeric = int.TryParse(left, out int leftNumericValue);
        bool rightNumeric = int.TryParse(right, out int rightNumericValue);

        if (leftNumeric && rightNumeric)
        {
            return leftNumericValue.CompareTo(rightNumericValue);
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ParsedVersion(int[] CoreIdentifiers, string[] PrereleaseIdentifiers);
}
