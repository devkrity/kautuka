using System.Text.RegularExpressions;

namespace Kautuka.SecurityTests;

/// <summary>Additional M4 auditability controls kept separate from projection ownership tests.</summary>
public sealed class ChannelRuntimeModularityArchitectureTests
{
    private static readonly string repositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProviderWorkersCannotUsePartialDeclarationsToHideResponsibility()
    {
        AssertNoPartialType("src/backend/Kautuka.ChannelHost/Providers/Slack", "SlackSocketModeWorker");
        AssertNoPartialType("src/backend/Kautuka.Gateway/RuntimePlane/Projection", "CanonicalRuntimeThreadProjector");
        AssertNoPartialType("src/backend/Kautuka.Gateway/Endpoints/Runtime", "NaradSseWriter");
    }

    [Fact]
    public void AllChannelHostProvidersRouteRuntimeEventsThroughSharedNoStreamTranslator()
    {
        string translator = Read("src/backend/Kautuka.ChannelHost/Hosting/ChannelRuntimeEventTranslator.cs");
        string executor = Read("src/backend/Kautuka.ChannelHost/Hosting/ChannelRuntimeEventOutboundExecutor.cs");
        string telegram = Read("src/backend/Kautuka.ChannelHost/Providers/Telegram/TelegramLongPollingWorker.Outbox.RuntimeEvent.cs");
        string slack = Read("src/backend/Kautuka.ChannelHost/Providers/Slack/SlackSocketModeWorker.cs");

        Assert.DoesNotContain("ChannelOutboundActionTypes.StreamMessage", translator, StringComparison.Ordinal);
        Assert.Contains("ChannelOutboundActionTypes.StreamMessage", executor, StringComparison.Ordinal);
        Assert.Contains("ChannelRuntimeEventTranslator.Shared.Translate", telegram, StringComparison.Ordinal);
        Assert.Contains("ChannelRuntimeEventOutboundExecutor.ExecuteGrpcAsync", slack, StringComparison.Ordinal);
    }

    [Fact]
    public void NaradSessionDeliveryOwnsBackfillLivePumpAndClientAttach()
    {
        string streamDelivery = Read("src/backend/Kautuka.Gateway/Endpoints/Runtime/NaradStreamDelivery.cs");
        string sessionWatch = Read("src/backend/Kautuka.Gateway/Endpoints/Runtime/NaradSessionWatchDelivery.cs");
        string endpoints = Read("src/backend/Kautuka.Gateway/Endpoints/Runtime/NaradEndpointMappings.cs");
        string leaseService = Read("src/backend/Kautuka.Gateway/Endpoints/Runtime/NaradClientLeaseService.cs");
        Assert.Contains("LoadBackfillThroughBoundary", streamDelivery, StringComparison.Ordinal);
        Assert.Contains("PumpLiveEventsAsync", streamDelivery, StringComparison.Ordinal);
        Assert.Contains("includeHydrate", streamDelivery, StringComparison.Ordinal);
        Assert.Contains("LoadBackfillThroughBoundary", sessionWatch, StringComparison.Ordinal);
        Assert.Contains("WriteWatchRevisionAsync", sessionWatch, StringComparison.Ordinal);
        Assert.Contains("/api/narad/v1/sessions/{sessionId}/watch", endpoints, StringComparison.Ordinal);
        Assert.Contains("/api/narad/v1/client/attach", endpoints, StringComparison.Ordinal);
        Assert.Contains("NaradClientLease Attach", leaseService, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatResyncMustNotGrowExternalSettleHeuristics()
    {
        string[] attachOwners =
        [
            "src/frontend/web/src/features/chat/kautuka/createKautukaNaradTransport.ts",
            "src/frontend/web/src/features/chat/kautuka/KautukaNaradProvider.tsx",
            "src/frontend/web/src/features/chat/kautuka/runSessionCatchUp.ts",
        ];
        string[] forbiddenMarkers =
        [
            "EXTERNAL_SETTLE_SNAPSHOT_DELAY_MS",
            "pendingSnapshotOnlyEpoch",
            "settleDespiteLive",
            "acceptedLocalPauseRootIds",
            "snapshotOnly",
            "locallyStartedRootRunIds",
            "LocalRootOwnershipSuppressesExternalProgress",
        ];
        foreach (string path in attachOwners)
        {
            string source = Read(path);
            foreach (string marker in forbiddenMarkers)
            {
                Assert.DoesNotContain(marker, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ChatContinuityContractIsLockedInAdrAndLifecycleDocs()
    {
        string adr = Read("docs/adr/0005-channel-runtime-projection-and-verification.md");
        string protocol = Read("docs/reference/agent-interaction-protocol.md");
        string frontend = Read("docs/architecture/frontend.md");
        string releaseGates = Read("docs/development/narad-release-gates.md");
        Assert.Contains("Chat continuity (root attach)", adr, StringComparison.Ordinal);
        Assert.Contains("LocalRootOwnershipSuppressesExternalProgress", adr, StringComparison.Ordinal);
        Assert.Contains("within **5 seconds**", adr, StringComparison.Ordinal);
        Assert.Contains("within **30 seconds**", adr, StringComparison.Ordinal);
        Assert.Contains("Subscribe before replay", protocol, StringComparison.Ordinal);
        Assert.Contains("Live attach and replay must avoid a gap", protocol, StringComparison.Ordinal);
        Assert.Contains("Root **attach** is the open-chat heartbeat", frontend, StringComparison.Ordinal);
        Assert.Contains("Forbidden: skipping attach because this browser", frontend, StringComparison.Ordinal);
        Assert.Contains("Narad release gates", releaseGates, StringComparison.Ordinal);
        Assert.Contains("Server history pagination", releaseGates, StringComparison.Ordinal);
    }

    [Fact]
    public void NaradConnectUsesBackfillValidationAndLiveTailCapture()
    {
        string streamDelivery = Read("src/backend/Kautuka.Gateway/Endpoints/Runtime/NaradStreamDelivery.cs");
        Assert.Contains("LoadBackfillThroughBoundary", streamDelivery, StringComparison.Ordinal);
        Assert.Contains("ValidateBackfill", streamDelivery, StringComparison.Ordinal);
        Assert.Contains("PumpLiveEventsAsync", streamDelivery, StringComparison.Ordinal);
        Assert.Contains("ReadNaradSessionProjectionRevision", streamDelivery, StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelProjectorFansUserPromptsAndApprovalContextInBothTetherModes()
    {
        string payload = Read("src/backend/Kautuka.Gateway/Domain/Channels/ChannelNaradProjectionPayload.cs");
        string translator = Read("src/backend/Kautuka.ChannelHost/Hosting/ChannelRuntimeEventTranslator.cs");
        Assert.Contains("UserMessageRecorded", payload, StringComparison.Ordinal);
        Assert.Contains("MessageUserRecorded", payload, StringComparison.Ordinal);
        Assert.Contains("UserMessageRecorded", translator, StringComparison.Ordinal);
        Assert.Contains("userMessage", Read("src/backend/Kautuka.Gateway/Domain/Channels/ChannelIngressService.ChannelApproval.cs"), StringComparison.Ordinal);
        Assert.Contains("prompt", translator, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditDocumentsDescribeEveryRequiredResilienceBoundaryAndOwner()
    {
        string resilience = Read("docs/development/channel-resilience.md");
        foreach (string boundary in new[] { "Gateway process", "ChannelHost process", "Provider simulator process", "Provider HTTP", "Provider WebSocket", "Gateway-to-ChannelHost mTLS", "Browser SSE", "Service dependency" })
            Assert.Contains(boundary, resilience, StringComparison.Ordinal);
        string services = Read("docs/reference/services.md");
        foreach (string owner in new[] { "Gateway runtime", "Runtime event publisher", "Runtime projection reducer", "Consumer attachment", "Narad projector/writer", "Gateway channel ingress", "Gateway channel projection", "ChannelHost authorization", "Provider adapter", "ChannelHost delivery store", "Provider simulator" })
            Assert.Contains(owner, services, StringComparison.Ordinal);
    }

    [Fact]
    public void NaradEventCatalogueDocumentsNativeAuthorityTypes()
    {
        string models = Read("src/shared/Narad.Contracts/NaradEventTypes.cs");
        Assert.Contains("public const string RunStarted", models, StringComparison.Ordinal);
        Assert.Contains("public const string MessageTextDelta", models, StringComparison.Ordinal);
        Assert.Contains("public const string ToolCallFinished", models, StringComparison.Ordinal);
        Assert.DoesNotContain("run.delivery.completed", models, StringComparison.Ordinal);
        Assert.DoesNotContain("tool.approval.", models, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptanceCodeKeepsResilienceQuantitiesExplicitAndReplayable()
    {
        string profile = Read("tests/channel-acceptance/src/resilience.ts");
        string evidence = Read("tests/channel-acceptance/src/evidence.ts");
        string replay = Read("tests/channel-acceptance/src/replay.ts");
        Assert.Contains("KAUTUKA_RESILIENCE_ITERATIONS", profile, StringComparison.Ordinal);
        Assert.Contains("KAUTUKA_RESILIENCE_CONCURRENCY", profile, StringComparison.Ordinal);
        Assert.Contains("KAUTUKA_RESILIENCE_MAX_DURATION_MS", profile, StringComparison.Ordinal);
        Assert.Contains("must be a positive integer supplied by the invoking policy", profile, StringComparison.Ordinal);
        Assert.Contains("replayCommand", evidence, StringComparison.Ordinal);
        Assert.Contains("KAUTUKA_ACCEPTANCE_SEED", replay, StringComparison.Ordinal);
    }

    private static void AssertNoPartialType(string directory, string typeName)
    {
        foreach (string source in Directory.EnumerateFiles(Path.Combine(repositoryRoot, directory), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText))
            Assert.DoesNotMatch($"partial\\s+class\\s+{Regex.Escape(typeName)}\\b", source);
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(repositoryRoot, relativePath));

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src/Kautuka.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
