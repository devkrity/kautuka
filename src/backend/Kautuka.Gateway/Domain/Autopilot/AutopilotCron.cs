namespace Kautuka.Gateway.Domain.Autopilot;

internal static class AutopilotCron
{
    public static DateTimeOffset? GetNextOccurrence(string cronExpression, TimeZoneInfo timeZone, DateTimeOffset nowUtc)
    {
        if (!TryParse(cronExpression, out CronField minute, out CronField hour, out CronField day, out CronField month, out CronField dayOfWeek))
        {
            return null;
        }

        DateTime localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;
        DateTime probe = new DateTime(localNow.Year, localNow.Month, localNow.Day, localNow.Hour, localNow.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);

        // Scan up to one year minute-by-minute for the next matching slot.
        for (int i = 0; i < 525_600; i++)
        {
            if (month.Contains(probe.Month)
                && day.Contains(probe.Day)
                && hour.Contains(probe.Hour)
                && minute.Contains(probe.Minute)
                && dayOfWeek.Contains((int)probe.DayOfWeek))
            {
                TimeSpan offset = timeZone.GetUtcOffset(probe);
                return new DateTimeOffset(probe, offset).ToUniversalTime();
            }

            probe = probe.AddMinutes(1);
        }

        return null;
    }

    private static bool TryParse(
        string expression,
        out CronField minute,
        out CronField hour,
        out CronField day,
        out CronField month,
        out CronField dayOfWeek)
    {
        minute = CronField.Empty;
        hour = CronField.Empty;
        day = CronField.Empty;
        month = CronField.Empty;
        dayOfWeek = CronField.Empty;

        string[] parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
        {
            return false;
        }

        return CronField.TryParse(parts[0], 0, 59, out minute)
            && CronField.TryParse(parts[1], 0, 23, out hour)
            && CronField.TryParse(parts[2], 1, 31, out day)
            && CronField.TryParse(parts[3], 1, 12, out month)
            && CronField.TryParse(parts[4], 0, 6, out dayOfWeek);
    }

    private readonly record struct CronField(bool[] Allowed)
    {
        public static CronField Empty => new([]);

        public bool Contains(int value)
        {
            if (Allowed.Length == 0 || value < 0 || value >= Allowed.Length)
            {
                return false;
            }

            return Allowed[value];
        }

        public static bool TryParse(string token, int min, int max, out CronField field)
        {
            bool[] allowed = new bool[max + 1];
            field = new CronField(allowed);

            foreach (string part in token.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParsePart(part, min, max, allowed))
                {
                    return false;
                }
            }

            field = new CronField(allowed);
            return true;
        }

        private static bool TryParsePart(string part, int min, int max, bool[] allowed)
        {
            string basePart = part;
            int step = 1;

            int slashIndex = part.IndexOf('/', StringComparison.Ordinal);
            if (slashIndex >= 0)
            {
                basePart = part[..slashIndex];
                string stepText = part[(slashIndex + 1)..];
                if (!int.TryParse(stepText, out step) || step <= 0)
                {
                    return false;
                }
            }

            int rangeStart;
            int rangeEnd;

            if (basePart == "*" || string.IsNullOrWhiteSpace(basePart))
            {
                rangeStart = min;
                rangeEnd = max;
            }
            else
            {
                int dashIndex = basePart.IndexOf('-', StringComparison.Ordinal);
                if (dashIndex >= 0)
                {
                    if (!int.TryParse(basePart[..dashIndex], out rangeStart)
                        || !int.TryParse(basePart[(dashIndex + 1)..], out rangeEnd))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!int.TryParse(basePart, out int value))
                    {
                        return false;
                    }

                    rangeStart = value;
                    rangeEnd = value;
                }
            }

            if (rangeStart < min || rangeEnd > max || rangeStart > rangeEnd)
            {
                return false;
            }

            for (int value = rangeStart; value <= rangeEnd; value += step)
            {
                allowed[value] = true;
            }

            return true;
        }
    }
}
