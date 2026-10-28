namespace Kautuka.Gateway.Infrastructure.Configuration;

// Persisted system_settings keys for sandbox defaults.
// Gateway reads these from Platform storage and publishes resolved runtime settings to Kautuka.Manager.
internal static class SandboxRuntimeKeys
{
    public const string RuntimeImageSettingKey = "infra.sandbox.image";
    public const string RuntimeDefaultCpuSettingKey = "infra.sandbox.default_cpus";
    public const string RuntimeDefaultMemorySettingKey = "infra.sandbox.default_memory";
    public const string RuntimeDefaultTimeoutSettingKey = "infra.sandbox.default_timeout";
}
