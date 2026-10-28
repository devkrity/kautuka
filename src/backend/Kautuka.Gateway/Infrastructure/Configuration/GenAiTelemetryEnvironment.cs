namespace Kautuka.Gateway.Infrastructure.Configuration;

internal static class GenAiTelemetryEnvironment
{
    public const string CaptureMessageContentEnvVar = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    public static void ApplyCaptureMessageContent(bool enabled)
    {
        Environment.SetEnvironmentVariable(
            CaptureMessageContentEnvVar,
            enabled ? "true" : "false",
            EnvironmentVariableTarget.Process);
    }
}
