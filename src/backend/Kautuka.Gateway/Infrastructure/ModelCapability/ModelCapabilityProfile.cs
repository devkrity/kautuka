namespace Kautuka.Gateway.Infrastructure.ModelCapability;

internal sealed class ModelCapabilityProfile
{
    public required ModelCapabilityKey Key { get; init; }
    public required DeclaredCapabilities Declared { get; init; }
    public required ProbedCapabilities Probed { get; init; }
    public required ObservedBehaviors Observed { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    private EffectiveCapabilities? effective;

    public EffectiveCapabilities GetEffective() =>
        effective ??= EffectiveCapabilities.Merge(Declared, Probed, Observed);

    public ModelCapabilityProfile WithObservation(ObservedBehaviors newObserved) =>
        new()
        {
            Key = Key,
            Declared = Declared,
            Probed = Probed,
            Observed = newObserved,
            CreatedAt = CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    public ModelCapabilityProfile WithProbed(ProbedCapabilities newProbed) =>
        new()
        {
            Key = Key,
            Declared = Declared,
            Probed = newProbed,
            Observed = Observed,
            CreatedAt = CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    public static ModelCapabilityProfile CreateNew(
        ModelCapabilityKey key,
        DeclaredCapabilities declared,
        ProbedCapabilities probed) =>
        new()
        {
            Key = key,
            Declared = declared,
            Probed = probed,
            Observed = new ObservedBehaviors(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
