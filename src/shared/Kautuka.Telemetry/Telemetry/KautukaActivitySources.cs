namespace Kautuka.Telemetry.Telemetry;

public static class KautukaActivitySources
{
    public const string GatewayAgentRunSource = "Kautuka.Gateway.AgentRun";
    public const string ProcessKindAttribute = "kautuka.process.kind";
    public const string ComponentAttribute = "kautuka.component";
    public const string TenantIdAttribute = "kautuka.tenant.id";
    public const string EndUserIdAttribute = "enduser.id";

#pragma warning disable CA1034
    public static class ProcessKind
    {
        public const string Gateway = "gateway";
        public const string Web = "web";
        public const string Channel = "channel";
        public const string Manager = "manager";
        public const string UpdateService = "updateservice";
        public const string Workspace = "workspace";
        public const string Credentials = "credentials";
    }

    public static class Component
    {
        public const string Gateway = "gateway";
        public const string WebHost = "webhost";
        public const string ChannelHost = "channelhost";
        public const string Manager = "manager";
        public const string UpdateService = "updateservice";
        public const string CredentialsHost = "credentialshost";
    }
#pragma warning restore CA1034
}
