using System.Collections.Concurrent;

using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    private const string channelMessageIdColumn = "channel_message_id";
    private const string channelTransportMessageIdColumn = "provider_message_id";

    private readonly ConcurrentDictionary<string, GatewaySession> sessions = new(StringComparer.Ordinal);
    private readonly List<GatewayRun> runs = [];
    private readonly ConcurrentDictionary<string, GatewayChannelInteraction> channelInteractions = new(StringComparer.Ordinal);
    private readonly object runLock = new();
    private readonly object runtimeLoadLock = new();
    private readonly object channelInteractionLoadLock = new();
    private readonly object settingsWriteLock = new();
    private readonly KautukaDatabase database;
    private readonly ILogger<GatewayState> logger;
    private readonly IAgentDefinitionStore agentDefinitionStore;
    private readonly IAgentRuntimeSessionRepository agentRuntimeSessionRepository;
    private readonly IAgentRuntimeRunRepository agentRuntimeRunRepository;
    private readonly IAgentMemoryFactRepository agentMemoryFactRepository;
    private readonly IGatewayMemoryStore memoryStore;
    private bool runtimeStateLoaded;
    private bool channelInteractionsLoaded;

    public ConcurrentDictionary<string, GatewayProvider> Providers { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, GatewayModel> Models { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, GatewayAgent> Agents { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> AgentStorageLocators { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, GatewayMcpServer> McpServers { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, GatewaySkill> Skills { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, GatewayUser> Users { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ExternalLoginLink> ExternalLogins { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte> PasswordlessUserIds { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, GatewayChannelRegistration> ChannelRegistrations { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, GatewayChannelIdentityLink> ChannelIdentityLinks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, GatewayChannelTargetBinding> ChannelTargetBindings { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, GatewayChannelInteraction> ChannelInteractions
    {
        get
        {
            EnsureChannelInteractionsLoaded();
            return channelInteractions;
        }
    }
    public GatewaySystemSettings SystemSettings { get; internal set; } = new();
    public GatewayOrganizationSettings OrganizationSettings { get; internal set; } = new();

    internal ConcurrentDictionary<string, GatewaySession> Sessions
    {
        get
        {
            EnsureRuntimeStateLoaded();
            return sessions;
        }
    }

    internal List<GatewayRun> Runs
    {
        get
        {
            EnsureRuntimeStateLoaded();
            return runs;
        }
    }
    internal object RunLock => runLock;
    internal IGatewayMemoryStore MemoryStore => memoryStore;
    internal IAgentDefinitionStore AgentDefinitionStore => agentDefinitionStore;
    internal IAgentRuntimeSessionRepository AgentRuntimeSessionRepository => agentRuntimeSessionRepository;
    internal IAgentRuntimeRunRepository AgentRuntimeRunRepository => agentRuntimeRunRepository;
    internal IAgentMemoryFactRepository AgentMemoryFactRepository => agentMemoryFactRepository;
    internal string? TopologyHash { get; set; }
    internal long DesiredStateVersion { get; set; }
    internal string? DesiredStateContentHash { get; set; }
    internal string DesiredStatePublisherEpoch { get; set; } = string.Empty;
    internal bool SetupCompleted { get; set; }
    internal KautukaDatabase Database => database;
    internal ILogger<GatewayState> Logger => logger;
    internal object SettingsWriteSyncRoot => settingsWriteLock;

    private GatewayState(
        KautukaDatabase database,
        ILogger<GatewayState> logger,
        IAgentDefinitionStore agentDefinitionStore,
        IAgentRuntimeSessionRepository agentRuntimeSessionRepository,
        IAgentRuntimeRunRepository agentRuntimeRunRepository,
        IAgentMemoryFactRepository agentMemoryFactRepository,
        IGatewayMemoryStore memoryStore)
    {
        this.database = database;
        this.logger = logger;
        this.agentDefinitionStore = agentDefinitionStore;
        this.agentRuntimeSessionRepository = agentRuntimeSessionRepository;
        this.agentRuntimeRunRepository = agentRuntimeRunRepository;
        this.agentMemoryFactRepository = agentMemoryFactRepository;
        this.memoryStore = memoryStore;
    }

    internal static GatewayState Create(
        KautukaDatabase database,
        ILogger<GatewayState> logger,
        IAgentDefinitionStore agentDefinitionStore,
        IAgentRuntimeSessionRepository agentRuntimeSessionRepository,
        IAgentRuntimeRunRepository agentRuntimeRunRepository,
        IAgentMemoryFactRepository agentMemoryFactRepository,
        IGatewayMemoryStore memoryStore) =>
        new(database, logger, agentDefinitionStore, agentRuntimeSessionRepository, agentRuntimeRunRepository, agentMemoryFactRepository, memoryStore);

    internal static void HydrateFromDatabase(SqliteConnection connection, GatewayState state, IWorkspaceRuntimeTool workspaceTool, IConfiguration configuration, IAgentDefinitionStore agentDefinitionStore)
    {
        LoadProviders(connection, state);
        LoadModels(connection, state);
        LoadAgents(connection, state, agentDefinitionStore);
        LoadMcpServers(connection, state);
        LoadSkills(connection, state);

        state.SystemSettings = LoadSettings(connection, configuration);
        state.OrganizationSettings = LoadOrganizationSettings(connection);
        state.TopologyHash = ReadSetting(connection, "topology_hash");
        state.DesiredStateVersion = long.TryParse(ReadSetting(connection, "desired_state.version"), out long desiredStateVersion)
            ? Math.Max(0, desiredStateVersion)
            : 0;
        state.DesiredStateContentHash = ReadSetting(connection, "desired_state.content_hash");
        state.DesiredStatePublisherEpoch = ReadSetting(connection, "desired_state.publisher_epoch") is { Length: > 0 } epoch
            ? epoch
            : Guid.NewGuid().ToString("D");
        state.SetupCompleted = string.Equals(ReadSetting(connection, "setup.completed"), "true", StringComparison.OrdinalIgnoreCase);

        LoadUsers(connection, state);
        state.runtimeStateLoaded = false;
        state.channelInteractionsLoaded = false;

        state.MemoryStore.Restore([]);
        LoadAgentMemoryFacts(connection, state);

        RebuildWorkspacesIfTopologyChanged(state, workspaceTool);
    }

    // ── factory ───────────────────────────────────────────────────────────────

    public static GatewayState FromDatabase(
        KautukaDatabase database,
        ILogger<GatewayState> logger,
        IGatewayMemoryStore memoryStore,
        IAgentDefinitionStore agentDefinitionStore,
        IConfiguration configuration,
        IWorkspaceRuntimeTool workspaceTool)
    {
        return GatewayStateFactory.FromDatabase(database, logger, memoryStore, agentDefinitionStore, configuration, workspaceTool);
    }

    internal void EnsureRuntimeStateLoaded()
    {
        if (runtimeStateLoaded)
        {
            return;
        }

        lock (runtimeLoadLock)
        {
            if (runtimeStateLoaded)
            {
                return;
            }

            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();

            LoadAgentSessionsAndRuns(connection, this);
            runtimeStateLoaded = true;
        }
    }

    internal void EnsureChannelInteractionsLoaded()
    {
        if (channelInteractionsLoaded)
        {
            return;
        }

        lock (channelInteractionLoadLock)
        {
            if (channelInteractionsLoaded)
            {
                return;
            }

            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();

            LoadChannelInteractions(connection, this);
            channelInteractionsLoaded = true;
        }
    }
}
