using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kautuka.ContractTests;

/// <summary>
/// G1 Architecture Contract Test (v7-32):
/// Roslyn AST scan of all Gateway and ChannelHost source files.
/// Asserts that every explicit and implicit construction of HttpClient, SocketsHttpHandler,
/// HttpClientHandler, ClientWebSocket and every AddHttpClient is strictly routed through
/// NetworkAccessCheckHandler, RemoteMcpRequestHandler, or NetworkAccessWebSocketChecker,
/// with only tightly-scoped mTLS loopback and probe-socket-factory exemptions.
/// </summary>
public sealed class NetworkAccessArchitectureContractTests
{
    private static readonly string gatewayRoot = ResolveProjectRoot("Kautuka.Gateway");
    private static readonly string channelHostRoot = ResolveProjectRoot("Kautuka.ChannelHost");

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "NAC review item test naming")]
    public void V7_21_OperationsGuideDoesNotClaimUnprovenTopologyOrTesting()
    {
        string repositoryRoot = Directory.GetParent(gatewayRoot)!.Parent!.Parent!.FullName;
        string guide = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "operations", "network-access.md"));

        Assert.DoesNotContain("Package Proxy (10.247.0.3:20101)", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime reconciliation supplies the launch identity", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("### 3.4 Validating Static Templates `[Built]`", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("### 2.1 Approval Cards `[Built]`", guide, StringComparison.Ordinal);
        Assert.Contains("nac-v7-production-rootless-run.json", guide, StringComparison.Ordinal);
        Assert.Contains("seven scenarios passed", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4/4 checks", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("E-rebind, E-parser, LAN grants, emergency-brake behavior", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acceptance remain pending", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[Tested]", guide, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "NAC review item test naming")]
    public void V7_21_ProductionAcceptanceReportIsCommittedAndEveryNegativeControlFlipped()
    {
        string repositoryRoot = Directory.GetParent(gatewayRoot)!.Parent!.Parent!.FullName;
        string reportPath = Path.Combine(repositoryRoot, "docs", "verification", "nac-v7-production-rootless-run.json");
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));

        Assert.Equal("97b66ca", report.RootElement.GetProperty("gitSha").GetString());
        Assert.Equal("7/7 PASS, 0 FAIL", report.RootElement.GetProperty("summary").GetString());

        JsonElement observations = report.RootElement.GetProperty("observations");
        Assert.Equal(7, observations.GetArrayLength());
        string[] expectedTestIds =
        [
            "PROD_E_PARSER",
            "PROD_E_REBIND",
            "PROD_LAN_GRANT",
            "PROD_CARD_AND_RULE",
            "PROD_V7_28_RECONCILE",
            "PROD_V7_29_VERIFIER",
            "PROD_V7_33_DNS_UPSTREAM",
        ];
        Assert.Equal(expectedTestIds, observations.EnumerateArray()
            .Select(static observation => observation.GetProperty("testId").GetString())
            .ToArray());
        Assert.All(observations.EnumerateArray(), static observation =>
        {
            Assert.Equal("PASS", observation.GetProperty("outcome").GetString());
            Assert.True(observation.GetProperty("ncFlipped").GetBoolean());
        });
    }

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "NAC review item test naming")]
    public async Task V7_21_ProductionRunUsesTheSameInitializerAsTheManagerHostedService()
    {
        string repositoryRoot = Directory.GetParent(gatewayRoot)!.Parent!.Parent!.FullName;
        string acceptancePath = Path.Combine(repositoryRoot, "src", "tests", "Kautuka.NetworkAccess.Acceptance", "Program.cs");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CompilationUnitSyntax source = CSharpSyntaxTree.ParseText(
            await File.ReadAllTextAsync(acceptancePath, cancellationToken),
            cancellationToken: cancellationToken).GetCompilationUnitRoot(cancellationToken);
        MethodDeclarationSyntax productionRun = source.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "RunProductionTopologyProofAsync");
        string[] calls = productionRun.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => string.Empty,
            })
            .ToArray();

        Assert.Contains("InitializeProductionAsync", calls, StringComparer.Ordinal);
        Assert.DoesNotContain("InitializeAsync", calls, StringComparer.Ordinal);
        Assert.Equal(5, calls.Count(call => string.Equals(call, "ResolveDesiredWorkspaceContext", StringComparison.Ordinal)));

        FinallyClauseSyntax cleanup = productionRun.DescendantNodes().OfType<FinallyClauseSyntax>().Last();
        string[] cleanupCalls = cleanup.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => string.Empty,
            })
            .ToArray();
        int topologyDisposeIndex = Array.IndexOf(cleanupCalls, "DisposeAsync");
        int podmanCleanupIndex = Array.IndexOf(cleanupCalls, "ExecAsync");
        Assert.True(topologyDisposeIndex >= 0 && topologyDisposeIndex < podmanCleanupIndex,
            "The topology reconcile loop must stop before the acceptance harness removes Podman resources.");
    }

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public void G1_AllOutboundHttpAndWebSocketUsages_AreCheckedOrAllowlisted()
    {
        List<string> allViolations = [];

        foreach (string file in EnumerateCsFiles(gatewayRoot))
        {
            string relPath = "Kautuka.Gateway/" + Path.GetRelativePath(gatewayRoot, file).Replace('\\', '/');
            allViolations.AddRange(ScanSourceText(relPath, File.ReadAllText(file)));
        }

        foreach (string file in EnumerateCsFiles(channelHostRoot))
        {
            string relPath = "Kautuka.ChannelHost/" + Path.GetRelativePath(channelHostRoot, file).Replace('\\', '/');
            allViolations.AddRange(ScanSourceText(relPath, File.ReadAllText(file)));
        }

        Assert.True(allViolations.Count == 0,
            $"Found {allViolations.Count} unwrapped or unallowlisted outbound network client usages:\n" +
            string.Join("\n", allViolations));
    }

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public void G1_NegativeControl_FlagsUnwrappedHttpClientAndClientWebSocket()
    {
        const string syntheticRogueSource = """
            using System.Net.Http;
            using System.Net.WebSockets;

            namespace RogueApp;

            public sealed class RogueClient
            {
                public void UncheckedCall()
                {
                    var client = new HttpClient();
                    var socket = new ClientWebSocket();
                }

                public void BareSocketsHandler()
                {
                    var handler = new SocketsHttpHandler();
                    var client2 = new HttpClient(handler);
                }
            }
            """;

        List<string> violations = ScanSourceText("Kautuka.Gateway/Rogue/SyntheticRogueClient.cs", syntheticRogueSource);

        Assert.True(violations.Count >= 4,
            $"Expected at least 4 violations in synthetic rogue source, but found {violations.Count}: {string.Join("; ", violations)}");
        Assert.Contains(violations, v => v.Contains("HttpClient", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("ClientWebSocket", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("SocketsHttpHandler", StringComparison.Ordinal));
    }

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public void G1_NegativeControl_FlagsPlantedBypassesAcrossPreviousLoopholes()
    {
        // Loophole 1: local variable literally named "handler" holding an unwrapped HttpClientHandler
        const string handlerNameBypass = """
            using System.Net.Http;
            namespace Bypass1;
            public sealed class BypassClient
            {
                public HttpClient Build()
                {
                    var handler = new HttpClientHandler();
                    return new HttpClient(handler);
                }
            }
            """;
        List<string> v1 = ScanSourceText("Kautuka.Gateway/Services/Bypass1.cs", handlerNameBypass);
        Assert.Contains(v1, v => v.Contains("HttpClient", StringComparison.Ordinal));
        Assert.Contains(v1, v => v.Contains("HttpClientHandler", StringComparison.Ordinal));

        // Loophole 2: ClientWebSocket calling socket.ConnectAsync directly (previously passed due to methodText.Contains("ConnectAsync"))
        const string wsDirectConnectBypass = """
            using System;
            using System.Net.WebSockets;
            using System.Threading;
            using System.Threading.Tasks;
            namespace Bypass2;
            public sealed class BypassWs
            {
                public async Task OpenAsync(Uri uri, CancellationToken ct)
                {
                    ClientWebSocket socket = new();
                    await socket.ConnectAsync(uri, ct);
                }
            }
            """;
        List<string> v2 = ScanSourceText("Kautuka.ChannelHost/Providers/BypassWs.cs", wsDirectConnectBypass);
        Assert.Contains(v2, v => v.Contains("ClientWebSocket", StringComparison.Ordinal));

        // Loophole 3: implicit target-typed HttpClient c = new(); (previously completely unscanned)
        const string implicitHttpClientBypass = """
            using System.Net.Http;
            namespace Bypass3;
            public sealed class BypassImplicit
            {
                public HttpClient Create()
                {
                    HttpClient client = new();
                    return client;
                }
            }
            """;
        List<string> v3 = ScanSourceText("Kautuka.Gateway/Services/BypassImplicit.cs", implicitHttpClientBypass);
        Assert.Contains(v3, v => v.Contains("HttpClient", StringComparison.Ordinal));

        // Loophole 4: rogue client placed inside Program.cs (previously blank-exempted via ApprovedInternalLoopbackFiles)
        const string programCsBypass = """
            using System.Net.Http;
            namespace Bypass4;
            public static class Entry
            {
                public static HttpClient Run() => new HttpClient(new SocketsHttpHandler());
            }
            """;
        List<string> v4 = ScanSourceText("Kautuka.Gateway/Program.cs", programCsBypass);
        Assert.Contains(v4, v => v.Contains("HttpClient", StringComparison.Ordinal));
        Assert.Contains(v4, v => v.Contains("SocketsHttpHandler", StringComparison.Ordinal));

        // Loophole 5: AddHttpClient("rogue") inside a file that calls ConfigureHttpClientDefaults on an unrelated builder
        const string addHttpClientBypass = """
            using Microsoft.Extensions.DependencyInjection;
            namespace Bypass5;
            public static class DiSetup
            {
                public static void Register(IServiceCollection services)
                {
                    services.AddHttpClient("unchecked-named");
                }
            }
            """;
        List<string> v5 = ScanSourceText("Kautuka.Gateway/Hosting/DiSetup.cs", addHttpClientBypass);
        Assert.Contains(v5, v => v.Contains("AddHttpClient", StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateCsFiles(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    internal static List<string> ScanSourceText(string repoRelativePath, string source)
    {
        List<string> violations = [];
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        SyntaxNode root = tree.GetRoot();
        string normalizedPath = repoRelativePath.Replace('\\', '/');

        foreach (SyntaxNode node in root.DescendantNodes())
        {
            if (node is ObjectCreationExpressionSyntax creation)
            {
                string typeName = ShortTypeName(creation.Type.ToString());
                int line = tree.GetLineSpan(creation.Span).StartLinePosition.Line + 1;
                CheckCreationNode(normalizedPath, line, typeName, creation, creation.ArgumentList, violations);
            }
            else if (node is ImplicitObjectCreationExpressionSyntax implicitCreation)
            {
                string? targetTypeName = ResolveImplicitTargetTypeName(implicitCreation);
                if (targetTypeName is null)
                {
                    continue;
                }

                int line = tree.GetLineSpan(implicitCreation.Span).StartLinePosition.Line + 1;
                CheckCreationNode(normalizedPath, line, ShortTypeName(targetTypeName), implicitCreation, implicitCreation.ArgumentList, violations);
            }
            else if (node is InvocationExpressionSyntax invocation
                && invocation.Expression is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name.Identifier.Text == "AddHttpClient")
            {
                int line = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
                if (!IsAddHttpClientChecked(invocation))
                {
                    violations.Add($"{normalizedPath}:{line} AddHttpClient without AddHttpMessageHandler<NetworkAccessCheckHandler>");
                }
            }
        }

        return violations;
    }

    private static void CheckCreationNode(
        string normalizedPath,
        int line,
        string typeName,
        ExpressionSyntax creationNode,
        ArgumentListSyntax? argumentList,
        List<string> violations)
    {
        if (typeName == "HttpClient")
        {
            if (!IsHttpClientCreationChecked(normalizedPath, creationNode, argumentList))
            {
                violations.Add($"{normalizedPath}:{line} Bare/unwrapped new HttpClient() without NetworkAccessCheckHandler or RemoteMcpRequestHandler");
            }
        }
        else if (typeName == "ClientWebSocket")
        {
            if (!IsClientWebSocketChecked(creationNode))
            {
                violations.Add($"{normalizedPath}:{line} ClientWebSocket without NetworkAccessWebSocketChecker.ConnectAsync");
            }
        }
        else if (typeName is "SocketsHttpHandler" or "HttpClientHandler")
        {
            if (!IsHttpHandlerCreationApproved(normalizedPath, typeName, creationNode))
            {
                violations.Add($"{normalizedPath}:{line} Bare new {typeName}() without NetworkAccessCheckHandler or scoped loopback mTLS");
            }
        }
    }

    private static bool IsHttpClientCreationChecked(
        string normalizedPath,
        ExpressionSyntax creationNode,
        ArgumentListSyntax? argumentList)
    {
        if (argumentList is null || argumentList.Arguments.Count == 0)
        {
            return false;
        }

        ExpressionSyntax firstArg = argumentList.Arguments[0].Expression;
        SyntaxNode enclosingScope = (SyntaxNode?)creationNode.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? (SyntaxNode?)creationNode.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()
            ?? creationNode.Ancestors().OfType<CompilationUnitSyntax>().First();

        return IsCheckedHandlerExpression(normalizedPath, firstArg, enclosingScope, depth: 0);
    }

    private static bool IsCheckedHandlerExpression(
        string normalizedPath,
        ExpressionSyntax expr,
        SyntaxNode enclosingScope,
        int depth)
    {
        if (depth > 4)
        {
            return false;
        }

        if (expr is ObjectCreationExpressionSyntax objCreate)
        {
            string handlerType = ShortTypeName(objCreate.Type.ToString());
            return handlerType is "NetworkAccessCheckHandler" or "RemoteMcpRequestHandler";
        }

        if (expr is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.CoalesceExpression))
        {
            return IsCheckedHandlerExpression(normalizedPath, binary.Right, enclosingScope, depth + 1)
                && IsAllowedCoalesceLeft(normalizedPath, binary.Left, enclosingScope, depth + 1);
        }

        if (expr is IdentifierNameSyntax identifier)
        {
            string idName = identifier.Identifier.Text;

            // Narrow parameter pass-through in the two internal factory/wrapper adapters whose public/internal
            // callers must still supply checked handlers (and which fail closed when omitted).
            if (normalizedPath == "Kautuka.Gateway/Infrastructure/AI/OpenAiCompatibleEndpoint.cs"
                && idName == "transportHandler")
            {
                return true;
            }

            if (normalizedPath == "Kautuka.Gateway/Infrastructure/Connectors/ConnectorAuthorizationClient.cs"
                && idName is "ownedHandler" or "handler")
            {
                return true;
            }

            // Trace local variable initializers and assignments to idName within the enclosing scope.
            var assignedExprs = new List<ExpressionSyntax>();
            foreach (VariableDeclaratorSyntax decl in enclosingScope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (decl.Identifier.Text == idName
                    && decl.Initializer is not null
                    && !(decl.Initializer.Value is LiteralExpressionSyntax litDecl && litDecl.IsKind(SyntaxKind.NullLiteralExpression)))
                {
                    assignedExprs.Add(decl.Initializer.Value);
                }
            }

            foreach (AssignmentExpressionSyntax assign in enclosingScope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assign.Left is IdentifierNameSyntax leftId && leftId.Identifier.Text == idName)
                {
                    ExpressionSyntax rhs = assign.Right;
                    if (rhs is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression))
                    {
                        continue;
                    }

                    assignedExprs.Add(rhs);
                }
            }

            return assignedExprs.Count > 0
                && assignedExprs.All(rhs => IsCheckedHandlerExpression(normalizedPath, rhs, enclosingScope, depth + 1));
        }

        return false;
    }

    private static bool IsAllowedCoalesceLeft(
        string normalizedPath,
        ExpressionSyntax left,
        SyntaxNode enclosingScope,
        int depth)
    {
        if (left is IdentifierNameSyntax id
            && normalizedPath == "Kautuka.Gateway/Infrastructure/AI/OpenAiCompatibleEndpoint.cs"
            && id.Identifier.Text == "transportHandler")
        {
            return true;
        }

        return IsCheckedHandlerExpression(normalizedPath, left, enclosingScope, depth);
    }

    private static bool IsClientWebSocketChecked(ExpressionSyntax creationNode)
    {
        SyntaxNode enclosingMember = (SyntaxNode?)creationNode.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? creationNode.Ancestors().OfType<CompilationUnitSyntax>().First();

        // Must invoke a checker method (*webSocketChecker*.ConnectAsync(...)) AND must not call raw socket.ConnectAsync directly
        var connectCalls = enclosingMember.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "ConnectAsync")
            .ToList();

        if (connectCalls.Count == 0)
        {
            return false;
        }

        return connectCalls.All(inv =>
        {
            string target = ((MemberAccessExpressionSyntax)inv.Expression).Expression.ToString();
            if (target.Contains("webSocketChecker", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Also verify local variables typed as NetworkAccessWebSocketChecker
            return enclosingMember.DescendantNodes().OfType<VariableDeclarationSyntax>().Any(vd =>
                ShortTypeName(vd.Type.ToString()) == "NetworkAccessWebSocketChecker"
                && vd.Variables.Any(v => v.Identifier.Text == target));
        });
    }

    private static bool IsHttpHandlerCreationApproved(
        string normalizedPath,
        string typeName,
        ExpressionSyntax creationNode)
    {
        // 1. Direct or coalesce argument to NetworkAccessCheckHandler / RemoteMcpRequestHandler
        if (creationNode.Ancestors().OfType<ObjectCreationExpressionSyntax>().Any(parentCreate =>
                ShortTypeName(parentCreate.Type.ToString()) is "NetworkAccessCheckHandler" or "RemoteMcpRequestHandler"))
        {
            return true;
        }

        // 2. Stored directly into a variable immediately passed into new NetworkAccessCheckHandler(..., var) in the same block
        if (creationNode.Parent is EqualsValueClauseSyntax eq
            && eq.Parent is VariableDeclaratorSyntax varDecl
            && creationNode.Ancestors().OfType<BlockSyntax>().FirstOrDefault() is { } block)
        {
            string varName = varDecl.Identifier.Text;
            bool passedToNacWrapper = block.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(parentCreate =>
                ShortTypeName(parentCreate.Type.ToString()) is "NetworkAccessCheckHandler" or "RemoteMcpRequestHandler"
                && parentCreate.ArgumentList?.Arguments.Any(arg => arg.Expression.ToString() == varName) == true);
            if (passedToNacWrapper)
            {
                return true;
            }
        }

        // 3. ConfigurePrimaryHttpMessageHandler in ConfigureHttpClientDefaults / AddHttpClient builder that also wires NetworkAccessCheckHandler
        if (creationNode.Ancestors().OfType<InvocationExpressionSyntax>().Any(inv =>
                inv.Expression is MemberAccessExpressionSyntax ma
                && ma.Name.Identifier.Text == "ConfigurePrimaryHttpMessageHandler"))
        {
            SyntaxNode scope = (SyntaxNode?)creationNode.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
                ?? creationNode.Ancestors().OfType<CompilationUnitSyntax>().First();
            if (scope.ToString().Contains("AddHttpMessageHandler<NetworkAccessCheckHandler>", StringComparison.Ordinal))
            {
                return true;
            }
        }

        // 4. Scoped internal loopback mTLS gRPC transport channels (must specify KautukaHostNetworkPolicy.CreateConnectCallback + TargetHost = "localhost")
        if (typeName == "SocketsHttpHandler"
            && normalizedPath is "Kautuka.Gateway/Hosting/GatewayAppBootstrap.cs" or "Kautuka.ChannelHost/Program.cs")
        {
            string initText = creationNode.ToString();
            if (initText.Contains("KautukaHostNetworkPolicy.CreateConnectCallback", StringComparison.Ordinal)
                && initText.Contains("TargetHost = \"localhost\"", StringComparison.Ordinal))
            {
                return true;
            }
        }

        // 5. Scoped probe & MCP runtime SocketsHttpHandler factory inside ProbeNetworkPolicy.cs
        if (typeName == "SocketsHttpHandler"
            && normalizedPath == "Kautuka.Gateway/Domain/Connectors/ProbeNetworkPolicy.cs")
        {
            string? methodName = creationNode.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
            if (methodName is "CreateProbeHandler" or "CreateRuntimeHandler")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAddHttpClientChecked(InvocationExpressionSyntax addHttpClientCall)
    {
        // Chained AddHttpMessageHandler<NetworkAccessCheckHandler>() on the same fluent builder expression
        foreach (InvocationExpressionSyntax ancestorCall in addHttpClientCall.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (ancestorCall.Expression is MemberAccessExpressionSyntax ma
                && ma.Name.ToString() == "AddHttpMessageHandler<NetworkAccessCheckHandler>")
            {
                return true;
            }
        }

        // Parameterless services.AddHttpClient() paired with ConfigureHttpClientDefaults(...AddHttpMessageHandler<NetworkAccessCheckHandler>()...)
        if (addHttpClientCall.ArgumentList.Arguments.Count == 0)
        {
            SyntaxNode scope = (SyntaxNode?)addHttpClientCall.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
                ?? addHttpClientCall.Ancestors().OfType<CompilationUnitSyntax>().First();
            foreach (InvocationExpressionSyntax defaultsCall in scope.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (defaultsCall.Expression is MemberAccessExpressionSyntax ma
                    && ma.Name.Identifier.Text == "ConfigureHttpClientDefaults"
                    && defaultsCall.ToString().Contains("AddHttpMessageHandler<NetworkAccessCheckHandler>", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ResolveImplicitTargetTypeName(ImplicitObjectCreationExpressionSyntax implicitCreation)
    {
        if (implicitCreation.Parent is EqualsValueClauseSyntax eq
            && eq.Parent is VariableDeclaratorSyntax vd
            && vd.Parent is VariableDeclarationSyntax vdecl)
        {
            return vdecl.Type.ToString();
        }

        if (implicitCreation.Parent is ArrowExpressionClauseSyntax arrow
            && arrow.Parent is MethodDeclarationSyntax method)
        {
            return method.ReturnType.ToString();
        }

        return null;
    }

    private static string ShortTypeName(string typeName)
    {
        int dot = typeName.LastIndexOf('.');
        return dot >= 0 ? typeName[(dot + 1)..] : typeName;
    }

    private static string ResolveProjectRoot(string projectName)
    {
        foreach (string origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(origin); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "src", "backend", projectName);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException($"Could not locate {projectName} project directory.");
    }
}
