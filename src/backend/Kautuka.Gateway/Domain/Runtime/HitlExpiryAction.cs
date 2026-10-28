namespace Kautuka.Gateway.Domain.Runtime;

internal enum HitlExpiryAction
{
    Abort = 0,
    Inform = 1,
    Retry = 2,
}

internal static class HitlExpiryActions
{
    public const string Abort = "abort";
    public const string Inform = "inform";
    public const string Retry = "retry";
    public const string Default = Abort;

    public static HitlExpiryAction Parse(string? raw, HitlExpiryAction fallback = HitlExpiryAction.Abort)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (string.Equals(raw.Trim(), Inform, StringComparison.OrdinalIgnoreCase))
        {
            return HitlExpiryAction.Inform;
        }

        if (string.Equals(raw.Trim(), Retry, StringComparison.OrdinalIgnoreCase))
        {
            return HitlExpiryAction.Retry;
        }

        if (string.Equals(raw.Trim(), Abort, StringComparison.OrdinalIgnoreCase))
        {
            return HitlExpiryAction.Abort;
        }

        return fallback;
    }

    public static string ToStorage(HitlExpiryAction action) => action switch
    {
        HitlExpiryAction.Inform => Inform,
        HitlExpiryAction.Retry => Retry,
        _ => Abort,
    };

    public static bool IsKnown(string? raw)
        => Parse(raw, (HitlExpiryAction)(-1)) is HitlExpiryAction.Abort or HitlExpiryAction.Inform or HitlExpiryAction.Retry;
}

internal static class HitlExpiryReasons
{
    public const string Inform = "Approval expired.";
    public const string Abort = "Approval expired; run aborted.";
    public const string RetryMarker = "hitl_expiry_retried";
}

internal static class RuntimeSettingKeys
{
    public const string ModelTimeoutSeconds = "runtime.model_timeout_seconds";
    public const string MaxRunDurationSeconds = "runtime.max_run_duration_seconds";
    public const string HitlExpirySeconds = "runtime.hitl_expiry_seconds";
    public const string HitlExpiryAction = "runtime.hitl_expiry_action";
    public const string AllowMultipleToolCalls = "runtime.allow_multiple_tool_calls";
    public const string AllowConcurrentInvocation = "runtime.allow_concurrent_invocation";
    public const string MaxParallelTools = "runtime.max_parallel_tools";

    public const int DefaultModelTimeoutSeconds = 300;
    public const int DefaultMaxRunDurationSeconds = 0;
    public const int DefaultHitlExpirySeconds = 0;
    public const bool DefaultAllowMultipleToolCalls = true;
    public const bool DefaultAllowConcurrentInvocation = false;
    public const int DefaultMaxParallelTools = 0;
    public const int MaxParallelToolsCap = 64;

    public static int ClampMaxParallelTools(int value) => Math.Clamp(value, 0, MaxParallelToolsCap);

    public static bool ParseBoolSetting(string? raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(raw.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }
}
