namespace Narad.Contracts;

/// <summary>Narad persisted record classes. Controls must never be written to the authority journal.</summary>
public static class NaradRecordClasses
{
    public const string Authority = "authority";
    public const string Watch = "watch";
    public const string Hydrate = "hydrate";
    public const string Control = "control";

    public static bool IsJournalRecordClass(string recordClass)
        => IsAuthorityRecordClass(recordClass);

    public static bool IsAuthorityRecordClass(string recordClass)
        => string.Equals(recordClass, Authority, StringComparison.Ordinal);

    public static void EnsureJournalAppendAllowed(string recordClass)
        => EnsureAuthorityOnly(recordClass);

    public static void EnsureAuthorityOnly(string recordClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordClass);
        if (string.Equals(recordClass, Control, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Narad control records must not be appended to the authority journal.");
        }

        if (string.Equals(recordClass, Watch, StringComparison.Ordinal)
            || string.Equals(recordClass, Hydrate, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Narad record class '{recordClass}' is not accepted by the authority journal.");
        }

        if (!IsAuthorityRecordClass(recordClass))
        {
            throw new ArgumentException($"Unknown Narad record class '{recordClass}'.", nameof(recordClass));
        }
    }
}
