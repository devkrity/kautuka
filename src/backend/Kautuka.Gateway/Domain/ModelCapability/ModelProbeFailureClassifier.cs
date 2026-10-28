namespace Kautuka.Gateway.Domain.ModelCapability;

internal static class ModelProbeFailureClassifier
{
    public static bool IsUnknownFinishReason(Exception exception)
        => exception is ArgumentOutOfRangeException outOfRange
            && string.Equals(outOfRange.ParamName, "value", StringComparison.Ordinal)
            && outOfRange.Message.Contains("Unknown ChatFinishReason value", StringComparison.Ordinal);
}
