using System.Diagnostics.CodeAnalysis;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kautuka.ContractTests;

/// <summary>
/// G1 Architecture Contract Test:
/// Roslyn syntax scan of all Gateway and ChannelHost source files.
/// Asserts that every construction of HttpClient, SocketsHttpHandler, HttpClientHandler, ClientWebSocket
/// and every AddHttpClient goes through the checked factory/handler or is explicitly listed in a reviewed allowlist.
/// Negative control G1.NC verifies that synthetic unwrapped clients are assertively caught.
/// </summary>
public sealed class NetworkAccessArchitectureContractTests
{
    private static readonly string gatewayRoot = ResolveProjectRoot("Kautuka.Gateway");
    private static readonly string channelHostRoot = ResolveProjectRoot("Kautuka.ChannelHost");

    private static readonly HashSet<string> ApprovedInternalLoopbackFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "GatewayAppBootstrap.cs", // Loopback mTLS SocketsHttpHandlers to internal Manager, UpdateService, ChannelHost, CredentialsHost
        "Program.cs",             // Loopback mTLS SocketsHttpHandler from ChannelHost to Manager
        "ProbeNetworkPolicy.cs",  // Factory creating underlying SocketsHttpHandler for probe/runtime sockets, wrapped by NetworkAccessCheckHandler
        "NetworkAccessCheckHandler.cs", // Fallback inner SocketsHttpHandler inside the handler itself
    };

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public void G1_AllOutboundHttpAndWebSocketUsages_AreCheckedOrAllowlisted()
    {
        List<string> allViolations = [];

        foreach (string file in Directory.EnumerateFiles(gatewayRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            allViolations.AddRange(ScanSourceFile(file));
        }

        foreach (string file in Directory.EnumerateFiles(channelHostRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            allViolations.AddRange(ScanSourceFile(file));
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

        List<string> violations = ScanSourceText("SyntheticRogueClient.cs", syntheticRogueSource);

        Assert.True(violations.Count >= 3,
            $"Expected at least 3 violations in synthetic rogue source, but found {violations.Count}: {string.Join("; ", violations)}");
        Assert.Contains(violations, v => v.Contains("HttpClient", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("ClientWebSocket", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("SocketsHttpHandler", StringComparison.Ordinal));
    }

    private static List<string> ScanSourceFile(string path)
    {
        string source = File.ReadAllText(path);
        return ScanSourceText(path, source);
    }

    private static List<string> ScanSourceText(string fileName, string source)
    {
        List<string> violations = [];
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        SyntaxNode root = tree.GetRoot();
        string fileBaseName = Path.GetFileName(fileName);
        bool isAllowlistedFile = ApprovedInternalLoopbackFiles.Contains(fileBaseName);

        // 1. Explicit creations: new HttpClient(...), new SocketsHttpHandler(...), etc.
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            string typeName = creation.Type.ToString();
            FileLinePositionSpan lineSpan = tree.GetLineSpan(creation.Span);
            int line = lineSpan.StartLinePosition.Line + 1;

            if (typeName == "HttpClient" || typeName.EndsWith(".HttpClient", StringComparison.Ordinal))
            {
                string creationText = creation.ToString();
                string enclosingText = creation.Parent?.ToString() ?? string.Empty;
                string grandParentText = creation.Parent?.Parent?.ToString() ?? string.Empty;

                bool isWrapped = creationText.Contains("NetworkAccessCheckHandler", StringComparison.Ordinal)
                    || enclosingText.Contains("NetworkAccessCheckHandler", StringComparison.Ordinal)
                    || enclosingText.Contains("handler", StringComparison.OrdinalIgnoreCase)
                    || enclosingText.Contains("transportHandler", StringComparison.Ordinal)
                    || enclosingText.Contains("transportOptions", StringComparison.Ordinal)
                    || grandParentText.Contains("NetworkAccessCheckHandler", StringComparison.Ordinal)
                    || isAllowlistedFile;

                if (!isWrapped)
                {
                    violations.Add($"{fileName}:{line} Bare new HttpClient() without NetworkAccessCheckHandler");
                }
            }
            else if (typeName == "ClientWebSocket" || typeName.EndsWith(".ClientWebSocket", StringComparison.Ordinal))
            {
                string methodText = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.ToString() ?? string.Empty;
                bool isChecked = methodText.Contains("webSocketChecker", StringComparison.OrdinalIgnoreCase)
                    || methodText.Contains("ConnectAsync", StringComparison.Ordinal)
                    || isAllowlistedFile;

                if (!isChecked)
                {
                    violations.Add($"{fileName}:{line} Bare new ClientWebSocket() without NetworkAccessWebSocketChecker");
                }
            }
            else if (typeName == "SocketsHttpHandler" || typeName.EndsWith(".SocketsHttpHandler", StringComparison.Ordinal)
                || typeName == "HttpClientHandler" || typeName.EndsWith(".HttpClientHandler", StringComparison.Ordinal))
            {
                MemberDeclarationSyntax? enclosingMember = creation.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                string enclosingText = enclosingMember?.ToString() ?? creation.Ancestors().Take(4).Aggregate(string.Empty, (acc, n) => acc + " " + n.ToString());
                bool isAllowed = isAllowlistedFile
                    || enclosingText.Contains("NetworkAccessCheckHandler", StringComparison.Ordinal)
                    || enclosingText.Contains("ProbeNetworkPolicy", StringComparison.Ordinal);

                if (!isAllowed)
                {
                    violations.Add($"{fileName}:{line} Bare new {typeName}() without NetworkAccessCheckHandler or internal loopback allowlist");
                }
            }
        }

        // 2. Implicit creations: ClientWebSocket socket = new(); etc.
        foreach (var implicitCreation in root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>())
        {
            string parentText = implicitCreation.Parent?.ToString() ?? string.Empty;
            FileLinePositionSpan lineSpan = tree.GetLineSpan(implicitCreation.Span);
            int line = lineSpan.StartLinePosition.Line + 1;

            if (parentText.Contains("ClientWebSocket", StringComparison.Ordinal))
            {
                string methodText = implicitCreation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.ToString() ?? string.Empty;
                bool isChecked = methodText.Contains("webSocketChecker", StringComparison.OrdinalIgnoreCase)
                    || methodText.Contains("ConnectAsync", StringComparison.Ordinal)
                    || isAllowlistedFile;

                if (!isChecked)
                {
                    violations.Add($"{fileName}:{line} Bare ClientWebSocket implicit creation without NetworkAccessWebSocketChecker");
                }
            }
            else if (parentText.Contains("SocketsHttpHandler", StringComparison.Ordinal))
            {
                string enclosingText = implicitCreation.Ancestors().Take(4).Aggregate(string.Empty, (acc, n) => acc + " " + n.ToString());
                bool isAllowed = isAllowlistedFile
                    || enclosingText.Contains("NetworkAccessCheckHandler", StringComparison.Ordinal)
                    || enclosingText.Contains("ProbeNetworkPolicy", StringComparison.Ordinal);

                if (!isAllowed)
                {
                    violations.Add($"{fileName}:{line} Bare SocketsHttpHandler implicit creation without NetworkAccessCheckHandler");
                }
            }
        }

        // 3. AddHttpClient invocations
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name.Identifier.Text == "AddHttpClient")
            {
                FileLinePositionSpan lineSpan = tree.GetLineSpan(invocation.Span);
                int line = lineSpan.StartLinePosition.Line + 1;

                string parentChain = invocation.Parent?.ToString() ?? string.Empty;
                string methodBlock = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.ToString() ?? string.Empty;

                bool isConfigured = parentChain.Contains("AddHttpMessageHandler<NetworkAccessCheckHandler>", StringComparison.Ordinal)
                    || methodBlock.Contains("ConfigureHttpClientDefaults", StringComparison.Ordinal)
                    || isAllowlistedFile;

                if (!isConfigured)
                {
                    violations.Add($"{fileName}:{line} AddHttpClient without AddHttpMessageHandler<NetworkAccessCheckHandler>");
                }
            }
        }

        return violations;
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
