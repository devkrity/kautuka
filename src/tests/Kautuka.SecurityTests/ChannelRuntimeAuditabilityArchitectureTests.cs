using System.Text.RegularExpressions;

namespace Kautuka.SecurityTests;

/// <summary>Source-level ownership ratchets for the channel/runtime boundary.</summary>
public sealed class ChannelRuntimeAuditabilityArchitectureTests
{
    private static readonly string repositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProviderAdaptersDoNotReferenceGatewayRuntimeAuthority() =>
        AssertFilesDoNotContain("src/backend/Kautuka.ChannelHost/Providers", "Kautuka.Gateway", "GatewayRuntimeEvent", "GatewayChatApproval");

    [Fact]
    public void ProjectionsRemainReadOnlyAndDoNotResolveApprovals() =>
        AssertFilesDoNotContain("src/backend/Kautuka.Gateway/RuntimePlane/Projection", "Domain.Stores", "AppendRuntimeEvent", "ResolveApproval", "ApprovalContinuationOrchestrator");

    [Fact]
    public void ExternalTransportsCannotResolveApprovalsOrCallCredentialAdministration() =>
        AssertFilesDoNotContain(
            "src/backend/Kautuka.ChannelHost",
            "ResolveApproval",
            "ApprovalContinuationOrchestrator",
            "IGatewayApprovalStore",
            "GatewayApprovalStore",
            "Kautuka.CredentialsHost",
            "CredentialsHostClient");

    [Fact]
    public void FrontendDoesNotCreateAlternateRuntimeApprovalOrReconnectTruth()
    {
        AssertFilesDoNotContain("src/frontend/web/src", "new WebSocket(", "new EventSource(", "GatewayRuntimeEventTypes");
        foreach (string file in SourceFiles("src/frontend/web/src", "*.ts", "*.tsx"))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotMatch(new Regex("(?:localStorage|sessionStorage).{0,80}(?:approval|runtime|reconnect)|(?:approval|runtime|reconnect).{0,80}(?:localStorage|sessionStorage)", RegexOptions.IgnoreCase), source);
        }
    }

    [Fact]
    public void NaradAuthorityCatalogueDocumentsNativeEventTypes()
    {
        string models = File.ReadAllText(Path.Combine(repositoryRoot, "src/shared/Narad.Contracts/NaradEventTypes.cs"));
        string protocol = File.ReadAllText(Path.Combine(repositoryRoot, "docs/reference/agent-interaction-protocol.md"));
        var constants = Regex.Matches(models, "public const string \\w+ = \\\"(?<event>[^\\\"]+)\\\";")
            .Select(match => match.Groups["event"].Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string required in new[]
        {
            "run.started",
            "run.finished",
            "tool.call.started",
            "tool.call.finished",
            "interrupt.requested",
            "message.text.delta",
        })
        {
            Assert.Contains(required, constants);
            Assert.Contains(required, protocol, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PrivilegedChannelControlsHaveTraceableEvidence()
    {
        string security = File.ReadAllText(Path.Combine(repositoryRoot, "docs/architecture/security.md"));
        foreach (string control in new[] { "Wrong local workload or CA-issued peer", "Missing, expired, replayed, or modified delegation", "Credential disclosure", "Runtime continuation initiated by transport", "Simulator endpoint admitted in production" })
            Assert.Contains(control, security, StringComparison.Ordinal);
        Assert.Contains("ChannelHostTrustBoundaryTests", security, StringComparison.Ordinal);
        Assert.Contains("approval continuation and channel ingress integration suites", security, StringComparison.Ordinal);
    }

    [Fact]
    public void ConceptualOwnersCannotGrowPastTheirCurrentAuditedBounds()
    {
        AssertConceptualOwner("CanonicalRuntimeThreadProjector", "src/backend/Kautuka.Gateway/RuntimePlane/Projection", 80, 1, 9, 8);
        AssertConceptualOwner("CanonicalRuntimeEventProjector", "src/backend/Kautuka.Gateway/RuntimePlane/Projection", 290, 1, 6, 0);
        AssertConceptualOwner("CanonicalRuntimeProjectionSession", "src/backend/Kautuka.Gateway/RuntimePlane/Projection", 120, 1, 17, 2);
        AssertConceptualOwner("CanonicalRunLifecycleProjector", "src/backend/Kautuka.Gateway/RuntimePlane/Projection", 110, 1, 7, 8);
        AssertConceptualOwner("CanonicalToolEventProjector", "src/backend/Kautuka.Gateway/RuntimePlane/Projection", 190, 1, 6, 8);
        AssertConceptualOwner("CanonicalApprovalResolutionActivityProjector", "src/backend/Kautuka.Gateway/RuntimePlane/Projection", 50, 1, 2, 8);
        AssertConceptualOwner("CanonicalUsageSummaryProjector", "src/backend/Kautuka.Gateway/RuntimePlane/Projection", 30, 1, 1, 8);
        AssertConceptualOwner("NaradSseWriter", "src/backend/Kautuka.Gateway/Endpoints/Runtime", 190, 1, 14, 0);
        AssertConceptualOwner("TelegramProtocolService", "src/tools/Kautuka.ProviderSimulator", 80, 1, 2, 2);
        AssertConceptualOwner("SlackProtocolService", "src/tools/Kautuka.ProviderSimulator", 80, 1, 4, 2);
    }

    [Fact]
    public void AuditabilityRatchetsHaveNoUndocumentedExceptionPath()
    {
        string decisions = File.ReadAllText(Path.Combine(repositoryRoot, "docs/architecture/auditability-decisions.md"));
        Assert.Contains("There are **no active exceptions**", decisions, StringComparison.Ordinal);
        Assert.Contains("create an ADR", decisions, StringComparison.Ordinal);
        Assert.Contains("cannot retroactively bless", decisions, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderSimulatorBootstrapOnlyComposesFocusedCollaborators()
    {
        string bootstrap = File.ReadAllText(Path.Combine(repositoryRoot, "src/tools/Kautuka.ProviderSimulator/SimulatorBootstrap.cs"));
        string program = File.ReadAllText(Path.Combine(repositoryRoot, "src/tools/Kautuka.ProviderSimulator/Program.cs"));
        Assert.True(program.Split('\n').Length <= 3, "Simulator Program must remain a composition entry point.");
        foreach (string collaborator in new[] { "SimulatorControlService", "SimulatorFaultExecutor", "TelegramProtocolService", "SlackProtocolService" })
            Assert.Contains(collaborator, bootstrap, StringComparison.Ordinal);
    }

    private static void AssertConceptualOwner(string typeName, string relativeDirectory, int maximumLines, int maximumConstructors, int maximumPublicMethods, int maximumConstructorDependencies)
    {
        string source = string.Join(Environment.NewLine, SourceFiles(relativeDirectory, $"{typeName}*.cs").Select(File.ReadAllText));
        int lines = source.Split('\n').Length;
        int constructors = Regex.Count(source, $"(?:public|internal|private)\\s+{Regex.Escape(typeName)}\\s*\\(");
        int publicMethods = Regex.Count(source, "\\b(?:public|internal)\\s+(?:static\\s+)?(?:async\\s+)?[A-Za-z_][A-Za-z0-9_<>,?\\[\\] ]*\\s+[A-Za-z_][A-Za-z0-9_]*\\s*\\(");
        Assert.True(lines <= maximumLines, $"{typeName} partial owner has {lines} lines; limit is {maximumLines}.");
        Assert.True(constructors <= maximumConstructors, $"{typeName} partial owner has {constructors} constructors; limit is {maximumConstructors}.");
        Assert.True(publicMethods <= maximumPublicMethods, $"{typeName} conceptual owner has {publicMethods} public/internal methods; limit is {maximumPublicMethods}.");
        Assert.True(MaximumConstructorDependencies(source, typeName) <= maximumConstructorDependencies, $"{typeName} conceptual owner has more than {maximumConstructorDependencies} constructor dependencies.");
    }

    private static int MaximumConstructorDependencies(string source, string typeName) => Regex.Matches(source, $"(?:class|record)\\s+{Regex.Escape(typeName)}\\s*\\((?<parameters>[^)]*)\\)|(?:public|internal|private)\\s+{Regex.Escape(typeName)}\\s*\\((?<parameters>[^)]*)\\)")
        .Select(match => match.Groups["parameters"].Value)
        .Select(parameters => string.IsNullOrWhiteSpace(parameters) ? 0 : parameters.Split(',').Length)
        .DefaultIfEmpty(0)
        .Max();

    private static void AssertFilesDoNotContain(string relativeDirectory, params string[] forbidden)
    {
        foreach (string file in SourceFiles(relativeDirectory, "*.cs", "*.ts", "*.tsx"))
            foreach (string value in forbidden)
                Assert.DoesNotContain(value, File.ReadAllText(file), StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles(string relativeDirectory, params string[] patterns) => patterns.SelectMany(pattern => Directory.EnumerateFiles(Path.Combine(repositoryRoot, relativeDirectory), pattern, SearchOption.AllDirectories));

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src/Kautuka.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Kautuka repository root.");
    }
}
