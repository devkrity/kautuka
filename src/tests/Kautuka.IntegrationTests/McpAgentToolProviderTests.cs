using System.Text;

using Grpc.Core;
using Grpc.Net.Client;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Infrastructure.Connectors;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Protos;
using Kautuka.Protos;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;

namespace Kautuka.IntegrationTests;

public sealed class McpAgentToolProviderTests
{
    [Fact]
    public void PersonalHttpOverrideDoesNotAllowPrivateConnectForASystemConnector()
    {
        Assert.False(McpAgentToolProvider.AllowsPrivateRuntimeDestination(GatewayOwnershipScopes.System, personalDestination: true, isAdmin: false));
        Assert.True(McpAgentToolProvider.AllowsPrivateRuntimeDestination(GatewayOwnershipScopes.System, personalDestination: false, isAdmin: false));
        Assert.False(McpAgentToolProvider.AllowsPrivateRuntimeDestination(GatewayOwnershipScopes.User, personalDestination: false, isAdmin: false));
        Assert.True(McpAgentToolProvider.AllowsPrivateRuntimeDestination(GatewayOwnershipScopes.User, personalDestination: true, isAdmin: true));
    }

    [Fact]
    public async Task HttpClientFactoryDoesNotDisposeTransportDuringClientCreation()
    {
        var transport = new CapturingAsyncDisposable();
        await using (transport.ConfigureAwait(true))
        {
            var factory = new McpHttpClientFactory(
                transportFactory: _ => transport,
                clientFactory: (createdTransport, _) =>
                {
                    Assert.Same(transport, createdTransport);
                    Assert.False(transport.DisposeAsyncCalled);
                    return Task.FromResult<McpClient>(null!);
                });

            McpClient? client = await factory.CreateAsync(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri("https://learn.microsoft.com/api/mcp", UriKind.Absolute),
                    Name = "probe",
                },
                allowPrivateDestination: false,
                CancellationToken.None);

            Assert.Null(client);
            Assert.False(transport.DisposeAsyncCalled);
        }
    }

    [Fact]
    public async Task BuildToolsAsyncAttemptsHttpConnectorWithoutSandbox()
    {
        var httpFactory = new CapturingMcpHttpClientFactory();
        var provider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "homeassistant-mcp",
                        Name = "Home Assistant",
                        Transport = McpTransport.Http,
                        HttpUrl = new Uri("https://ha.example.com/api/mcp", UriKind.Absolute),
                        Enabled = true,
                    },
                ],
            }),
            CreateNoOpCredentialResolver(),
            httpFactory,
            new ThrowingWorkspaceGrpcService(),
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);

        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition
                {
                    Enabled = false,
                },
                McpBindings =
                [
                    new AgentMcpBindingDefinition
                    {
                        ConnectorId = "homeassistant-mcp",
                        Context = "OwnContext",
                    },
                ],
            },
            "agent-alpha",
            UserId: null,
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null));

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None)];

        Assert.Empty(tools);
        Assert.True(httpFactory.WasCalled);
        Assert.Equal(0, workspaceTool.SpawnAgentSandboxCallCount);
    }

    [Fact]
    public async Task BuildToolsAsyncSkipsAnotherUsersConnector()
    {
        var foreignFactory = new CapturingMcpHttpClientFactory();
        var foreignProvider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "foreign-mcp",
                        Name = "Foreign",
                        Transport = McpTransport.Http,
                        HttpUrl = new Uri("https://foreign.example.com/api/mcp", UriKind.Absolute),
                        Enabled = true,
                        Scope = GatewayOwnershipScopes.User,
                        OwnerUserId = "user-b",
                    },
                ],
            }),
            CreateNoOpCredentialResolver(),
            foreignFactory,
            new ThrowingWorkspaceGrpcService(),
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> foreignTools = [.. await foreignProvider.BuildToolsAsync(
            CreateHttpConnectorContext("foreign-mcp", "user-a"),
            CancellationToken.None)];

        Assert.Empty(foreignTools);
        Assert.False(foreignFactory.WasCalled);

        var ownFactory = new CapturingMcpHttpClientFactory();
        var ownCredentials = new ScopeAwareCredentialsClient();
        var ownProvider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "own-mcp",
                        Name = "Own",
                        Transport = McpTransport.Http,
                        HttpUrl = new Uri("https://own.example.com/api/mcp", UriKind.Absolute),
                        Enabled = true,
                        Scope = GatewayOwnershipScopes.User,
                        OwnerUserId = "user-a",
                    },
                ],
            }),
            new CredentialResolverService(new Lazy<CredentialsService.CredentialsServiceClient>(() => ownCredentials)),
            ownFactory,
            new ThrowingWorkspaceGrpcService(),
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);

        _ = await ownProvider.BuildToolsAsync(CreateHttpConnectorContext("own-mcp", "user-a"), CancellationToken.None);

        Assert.True(ownFactory.WasCalled);
    }

    [Fact]
    public async Task BuildToolsAsyncAttemptsContainerConnectorWithoutSandbox()
    {
        var httpFactory = new CapturingMcpHttpClientFactory();
        var grpcService = new CapturingWorkspaceGrpcService();
        var provider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "container-mcp",
                        Name = "container",
                        Transport = McpTransport.Container,
                        ContainerImage = "ghcr.io/example/container-mcp:latest",
                        Enabled = true,
                    },
                ],
            }),
            CreateNoOpCredentialResolver(),
            httpFactory,
            grpcService,
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);

        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition
                {
                    Enabled = false,
                },
                McpBindings =
                [
                    new AgentMcpBindingDefinition
                    {
                        ConnectorId = "container-mcp",
                        Context = "OwnContext",
                    },
                ],
            },
            "agent-alpha",
            UserId: null,
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null));

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None)];

        Assert.Empty(tools);
        Assert.True(grpcService.ListToolsWasCalled);
        Assert.False(httpFactory.WasCalled);
        Assert.Equal(0, workspaceTool.SpawnAgentSandboxCallCount);
    }

    [Fact]
    public async Task BuildToolsAsyncUsesExplicitBindingsInsteadOfLegacyConnectorIds()
    {
        var httpFactory = new CapturingMcpHttpClientFactory();
        var provider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "binding-http",
                        Name = "Binding HTTP",
                        Transport = McpTransport.Http,
                        HttpUrl = new Uri("https://binding.example.com/api/mcp", UriKind.Absolute),
                        Enabled = true,
                    },
                    new McpServerDefinition
                    {
                        Id = "legacy-container",
                        Name = "Legacy Container",
                        Transport = McpTransport.Container,
                        ContainerImage = "ghcr.io/example/legacy:latest",
                        Enabled = true,
                    },
                ],
            }),
            CreateNoOpCredentialResolver(),
            httpFactory,
            new ThrowingWorkspaceGrpcService(),
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);

        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition
                {
                    Enabled = false,
                },
                McpBindings =
                [
                    new AgentMcpBindingDefinition
                    {
                        ConnectorId = "binding-http",
                        Context = "OwnContext",
                    },
                ],
            },
            "agent-alpha",
            UserId: null,
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null));

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None)];

        Assert.Empty(tools);
        Assert.True(httpFactory.WasCalled);
        Assert.Equal(0, workspaceTool.SpawnAgentSandboxCallCount);
    }

    [Fact]
    public async Task BuildToolsAsyncIncludesBoundInternalSystemConnectorsWhenDisabled()
    {
        var grpcService = new CapturingWorkspaceGrpcService();
        var provider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "kautuka-web-tools",
                        Name = "Kautuka Web Tools",
                        Transport = McpTransport.Container,
                        ContainerImage = "ghcr.io/example/kautuka-web-tools:latest",
                        Enabled = false,
                        IsInternalSystemConnector = true,
                    },
                ],
            }),
            CreateNoOpCredentialResolver(),
            new CapturingMcpHttpClientFactory(),
            grpcService,
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);

        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition
                {
                    Enabled = false,
                },
                McpBindings =
                [
                    new AgentMcpBindingDefinition
                    {
                        ConnectorId = "kautuka-web-tools",
                        Context = "OwnContext",
                    },
                ],
            },
            "agent-alpha",
            UserId: null,
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null));

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None)];

        Assert.Empty(tools);
        Assert.True(grpcService.ListToolsWasCalled);
        Assert.True(grpcService.LastListToolsRequest?.AllowInternalSystemConnector);
        Assert.Equal("system::kautuka-web-tools", grpcService.LastListToolsRequest?.ConnectorId);
    }

    [Fact]
    public async Task BuildToolsAsyncUsesIsolatedWithAgentFilesExecutionContextForBinding()
    {
        var grpcService = new CapturingWorkspaceGrpcService();
        var provider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "bundle-mcp",
                        Name = "Bundle MCP",
                        Transport = McpTransport.Container,
                        ContainerImage = "ghcr.io/example/bundle:latest",
                        Enabled = true,
                    },
                ],
            }),
            CreateNoOpCredentialResolver(),
            new CapturingMcpHttpClientFactory(),
            grpcService,
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);

        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition { Enabled = true },
                McpBindings =
                [
                    new AgentMcpBindingDefinition
                    {
                        ConnectorId = "bundle-mcp",
                        Context = "OwnContextWithAgentFiles",
                    },
                ],
            },
            "agent-alpha",
            UserId: "user-1",
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", "user-1", null));

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None)];

        Assert.Empty(tools);
        Assert.Equal(WorkspaceMcpExecutionContext.IsolatedWithAgentFiles, grpcService.LastListToolsRequest?.ExecutionContext);
        Assert.Equal("/workspace", grpcService.LastListToolsRequest?.WorkspaceContext?.ContainerWorkspacePath);
    }

    [Fact]
    public async Task BuildToolsAsyncUsesAgentSandboxExecutionContextForBinding()
    {
        var grpcService = new CapturingWorkspaceGrpcService();
        var provider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "bundle-mcp",
                        Name = "Bundle MCP",
                        Transport = McpTransport.Container,
                        ContainerImage = "ghcr.io/example/bundle:latest",
                        Enabled = true,
                    },
                ],
            }),
            CreateNoOpCredentialResolver(),
            new CapturingMcpHttpClientFactory(),
            grpcService,
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);

        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition { Enabled = true },
                McpBindings =
                [
                    new AgentMcpBindingDefinition
                    {
                        ConnectorId = "bundle-mcp",
                        Context = "AgentContext",
                    },
                ],
            },
            "agent-alpha",
            UserId: "user-1",
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", "user-1", null));

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None)];

        Assert.Empty(tools);
        Assert.Equal(WorkspaceMcpExecutionContext.AgentSandbox, grpcService.LastListToolsRequest?.ExecutionContext);
        Assert.Equal("/workspace", grpcService.LastListToolsRequest?.WorkspaceContext?.ContainerWorkspacePath);
    }

    [Fact]
    public async Task BuildToolsAsyncSkipsAgentSandboxBindingWhenSandboxDisabled()
    {
        var grpcService = new CapturingWorkspaceGrpcService();
        var provider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "bundle-mcp",
                        Name = "Bundle MCP",
                        Transport = McpTransport.Container,
                        ContainerImage = "ghcr.io/example/bundle:latest",
                        Enabled = true,
                    },
                ],
            }),
            CreateNoOpCredentialResolver(),
            new CapturingMcpHttpClientFactory(),
            grpcService,
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);

        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition { Enabled = false },
                McpBindings =
                [
                    new AgentMcpBindingDefinition
                    {
                        ConnectorId = "bundle-mcp",
                        Context = "AgentContext",
                    },
                ],
            },
            "agent-alpha",
            UserId: "user-1",
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", "user-1", null));

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None)];

        Assert.Empty(tools);
        Assert.Null(grpcService.LastListToolsRequest);
    }

    [Fact]
    public void PolicyDeniesUncategorizedCategoryByDefault()
    {
        var policyService = new ToolExecutionPolicyService();
        var agent = new AgentDefinition
        {
            Sandbox = new AgentSandboxDefinition { Enabled = true },
            SuperExecMode = "trusted",
        };

        ToolPolicyDecision decision = policyService.EvaluateExposure(ToolPolicyCategory.Other, agent);

        Assert.False(decision.Allowed);
        Assert.Equal(ToolPolicyReasonCode.UncategorizedCategoryDenied, decision.ReasonCode);
    }

    [Fact]
    public void PolicyDeniesUnknownCategoryByDefault()
    {
        var policyService = new ToolExecutionPolicyService();
        var agent = new AgentDefinition
        {
            Sandbox = new AgentSandboxDefinition { Enabled = true },
            SuperExecMode = "trusted",
        };

        ToolPolicyDecision decision = policyService.EvaluateExposure((ToolPolicyCategory)999, agent);

        Assert.False(decision.Allowed);
        Assert.Equal(ToolPolicyReasonCode.UnknownCategoryDenied, decision.ReasonCode);
    }

    [Fact]
    public void EvaluateApprovalDisablesConnectorFunctionWhenExplicitlyDisabled()
    {
        var policyService = new ToolExecutionPolicyService();
        var policy = new ApprovalPolicy
        {
            DisabledMcpFunctions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["docs"] = ["parse_document"],
            },
        };

        ToolApprovalDirective directive = policyService.EvaluateApproval(new ToolApprovalRequest(
            Identity: new ToolIdentity(ToolPolicyCategory.Connector, "parse_document", "docs"),
            ApprovalPolicy: policy));

        Assert.False(directive.Include);
        Assert.False(directive.RequiresApproval);
    }

    [Fact]
    public void EvaluateApprovalRequiresForSuperExecWhenAgentIsAskMode()
    {
        var policyService = new ToolExecutionPolicyService();
        var agent = new AgentDefinition
        {
            SuperExecMode = "ask",
            Sandbox = new AgentSandboxDefinition { Enabled = true },
        };

        ToolApprovalDirective directive = policyService.EvaluateApproval(new ToolApprovalRequest(
            Identity: new ToolIdentity(ToolPolicyCategory.SuperExec, "run_shell"),
            Agent: agent));

        Assert.True(directive.Include);
        Assert.True(directive.RequiresApproval);
    }

    [Fact]
    public void EvaluateApprovalRequiresForSkillsWhenSkillModeRequiresApproval()
    {
        var policyService = new ToolExecutionPolicyService();

        ToolApprovalDirective directive = policyService.EvaluateApproval(new ToolApprovalRequest(
            Identity: new ToolIdentity(ToolPolicyCategory.Skill, "read_skill_resource"),
            SkillMode: SkillAttachmentMode.RequireApproval));

        Assert.True(directive.Include);
        Assert.True(directive.RequiresApproval);
    }

    [Fact]
    public void EvaluateApprovalRequiresForAgentToolWhenTargetAgentIsApprovalRequired()
    {
        var policyService = new ToolExecutionPolicyService();
        var caller = new AgentDefinition
        {
            ApprovalPolicy = new ApprovalPolicy
            {
                ApprovalRequiredAgentToolIds = ["agent-beta"],
            },
        };

        ToolApprovalDirective directive = policyService.EvaluateApproval(new ToolApprovalRequest(
            Identity: new ToolIdentity(ToolPolicyCategory.Other, "invoke_agent_agent_beta"),
            Agent: caller,
            TargetAgentId: "agent-beta"));

        Assert.True(directive.Include);
        Assert.True(directive.RequiresApproval);
    }

    [Fact]
    public void EvaluateConnectorExposureDeniesAgentSandboxModeWhenAgentSandboxDisabled()
    {
        var policyService = new ToolExecutionPolicyService();
        var agent = new AgentDefinition
        {
            Sandbox = new AgentSandboxDefinition { Enabled = false },
        };

        ToolPolicyDecision decision = policyService.EvaluateConnectorExposure(agent, WorkspaceMcpExecutionContext.AgentSandbox);

        Assert.False(decision.Allowed);
        Assert.Equal(ToolPolicyReasonCode.SandboxRequired, decision.ReasonCode);
    }

    [Fact]
    public void EvaluateConnectorExposureAllowsIsolatedWithAgentFilesWhenAgentSandboxDisabled()
    {
        var policyService = new ToolExecutionPolicyService();
        var agent = new AgentDefinition
        {
            Sandbox = new AgentSandboxDefinition { Enabled = false },
        };

        ToolPolicyDecision decision = policyService.EvaluateConnectorExposure(agent, WorkspaceMcpExecutionContext.IsolatedWithAgentFiles);

        Assert.True(decision.Allowed);
        Assert.Equal(ToolPolicyReasonCode.Allowed, decision.ReasonCode);
    }

    [Fact]
    public async Task CatalogHitSkipsHttpFactoryAndContainerListTools()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var httpFactory = new CapturingMcpHttpClientFactory();
        var grpcService = new CapturingWorkspaceGrpcService { SucceedListTools = true };
        var inspect = new CountingSkillPackageService();
        AgentDefinition agent = CreateBoundHttpAgent();
        PlatformConfiguration config = CreateHttpPlatform(agent);
        AgentToolCatalog catalog = CreateCatalog(testStore, config, httpFactory, grpcService, inspect);

        string fingerprint = AgentToolingFingerprint.Compute(agent, config);
        testStore.SurfaceStore.Save(agent.Id, fingerprint, new AgentToolSurfaceCatalogue
        {
            Entries =
            [
                new AgentToolSurfaceEntry
                {
                    Source = AgentToolSurfaceSources.Mcp,
                    SourceId = "homeassistant-mcp",
                    Name = "ha_status",
                    Description = "Home Assistant status",
                    JsonSchema = """{"type":"object"}""",
                },
                new AgentToolSurfaceEntry
                {
                    Source = AgentToolSurfaceSources.Skill,
                    SourceId = "load_skill",
                    Name = "load_skill",
                    Description = "Load skill",
                    JsonSchema = """{"type":"object"}""",
                },
            ],
        });

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = await catalog.BuildToolsAsync(
            CreateHttpToolContext(agent),
            CancellationToken.None);

        Assert.Contains(tools, tool => tool.Name == "ha_status");
        Assert.False(httpFactory.WasCalled);
        Assert.Equal(0, grpcService.ListToolsCallCount);
        Assert.Equal(0, inspect.InspectCount);
    }

    [Fact]
    public async Task CatalogMissPersistsSuccessfulContainerListAndReusesIt()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var httpFactory = new CapturingMcpHttpClientFactory();
        var grpcService = new CapturingWorkspaceGrpcService { SucceedListTools = true };
        AgentDefinition agent = CreateBoundContainerAgent();
        PlatformConfiguration config = CreateContainerPlatform(agent, "ghcr.io/example/container-mcp:latest");
        AgentToolCatalog catalog = CreateCatalog(testStore, config, httpFactory, grpcService, new CountingSkillPackageService());
        AgentToolContext context = CreateContainerToolContext(agent);

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> first = await catalog.BuildToolsAsync(context, CancellationToken.None);
        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> second = await catalog.BuildToolsAsync(context, CancellationToken.None);

        Assert.Contains(first, tool => tool.Name == "search");
        Assert.Contains(second, tool => tool.Name == "search");
        Assert.Equal(1, grpcService.ListToolsCallCount);
        Assert.False(httpFactory.WasCalled);
    }

    [Fact]
    public async Task CachedToolsAreNotReusedForAnotherCaller()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var grpcService = new CapturingWorkspaceGrpcService { SucceedListTools = true, ListedToolName = "owner-tool" };
        AgentDefinition agent = CreateBoundContainerAgent();
        PlatformConfiguration config = new()
        {
            Agents = [agent],
            McpServers =
            [
                new McpServerDefinition
                {
                    Id = "container-mcp",
                    Name = "container",
                    Transport = McpTransport.Container,
                    ContainerImage = "ghcr.io/example/container-mcp:latest",
                    Enabled = true,
                    Scope = GatewayOwnershipScopes.User,
                    OwnerUserId = "user-a",
                },
            ],
        };
        AgentToolCatalog catalog = CreateCatalog(testStore, config, new CapturingMcpHttpClientFactory(), grpcService, new CountingSkillPackageService());

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> ownerTools = await catalog.BuildToolsAsync(
            CreateContainerToolContext(agent, "user-a"),
            CancellationToken.None);
        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> otherTools = await catalog.BuildToolsAsync(
            CreateContainerToolContext(agent, "user-b"),
            CancellationToken.None);

        Assert.Contains(ownerTools, tool => tool.Name == "owner-tool");
        Assert.DoesNotContain(otherTools, tool => tool.Name == "owner-tool");
        Assert.Equal(1, grpcService.ListToolsCallCount);
    }

    [Fact]
    public async Task SharedConnectorDiscoveryIsCachedPerCaller()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var grpcService = new CapturingWorkspaceGrpcService { SucceedListTools = true, ListedToolName = "caller-a-tool" };
        AgentDefinition agent = CreateBoundContainerAgent();
        PlatformConfiguration config = CreateContainerPlatform(agent, "ghcr.io/example/container-mcp:latest");
        AgentToolCatalog catalog = CreateCatalog(testStore, config, new CapturingMcpHttpClientFactory(), grpcService, new CountingSkillPackageService());

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> firstCaller = await catalog.BuildToolsAsync(
            CreateContainerToolContext(agent, "user-a"),
            CancellationToken.None);
        grpcService.ListedToolName = "caller-b-tool";
        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> secondCaller = await catalog.BuildToolsAsync(
            CreateContainerToolContext(agent, "user-b"),
            CancellationToken.None);

        Assert.Contains(firstCaller, tool => tool.Name == "caller-a-tool");
        Assert.Contains(secondCaller, tool => tool.Name == "caller-b-tool");
        Assert.DoesNotContain(secondCaller, tool => tool.Name == "caller-a-tool");
        Assert.Equal(2, grpcService.ListToolsCallCount);
    }

    [Fact]
    public async Task CachedConnectorToolIsDroppedWhenCallerCannotUseIt()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var grpcService = new CapturingWorkspaceGrpcService { SucceedListTools = true, ListedToolName = "fresh-tool" };
        AgentDefinition agent = CreateBoundContainerAgent();
        PlatformConfiguration config = new()
        {
            Agents = [agent],
            McpServers =
            [
                new McpServerDefinition
                {
                    Id = "container-mcp",
                    Name = "container",
                    Transport = McpTransport.Container,
                    ContainerImage = "ghcr.io/example/container-mcp:latest",
                    Enabled = true,
                    Scope = GatewayOwnershipScopes.User,
                    OwnerUserId = "user-a",
                },
            ],
        };
        string fingerprint = AgentToolingFingerprint.WithCaller(
            AgentToolingFingerprint.Compute(agent, config),
            "user-b",
            []);
        testStore.SurfaceStore.Save(agent.Id, fingerprint, new AgentToolSurfaceCatalogue
        {
            Entries =
            [
                new AgentToolSurfaceEntry
                {
                    Source = AgentToolSurfaceSources.Mcp,
                    SourceId = "container-mcp",
                    Name = "leaked-tool",
                    Description = "Cached for another owner",
                    JsonSchema = """{"type":"object"}""",
                },
            ],
        });
        AgentToolCatalog catalog = CreateCatalog(testStore, config, new CapturingMcpHttpClientFactory(), grpcService, new CountingSkillPackageService());

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = await catalog.BuildToolsAsync(
            CreateContainerToolContext(agent, "user-b"),
            CancellationToken.None);

        Assert.DoesNotContain(tools, tool => tool.Name == "leaked-tool");
        Assert.Equal(0, grpcService.ListToolsCallCount);
    }

    [Fact]
    public async Task CatalogHitSurvivesPersonaOnlyChange()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var grpcService = new CapturingWorkspaceGrpcService { SucceedListTools = true };
        AgentDefinition agent = CreateBoundContainerAgent();
        PlatformConfiguration config = CreateContainerPlatform(agent, "ghcr.io/example/container-mcp:latest");
        AgentToolCatalog catalog = CreateCatalog(testStore, config, new CapturingMcpHttpClientFactory(), grpcService, new CountingSkillPackageService());
        await catalog.BuildToolsAsync(CreateContainerToolContext(agent), CancellationToken.None);

        AgentDefinition personaChanged = new()
        {
            Id = agent.Id,
            Name = "Renamed",
            SystemPrompt = "different",
            Persona = new System.Text.Json.Nodes.JsonObject { ["tone"] = "formal" },
            Sandbox = agent.Sandbox,
            McpBindings = agent.McpBindings,
        };
        await catalog.BuildToolsAsync(CreateContainerToolContext(personaChanged), CancellationToken.None);

        Assert.Equal(1, grpcService.ListToolsCallCount);
    }

    [Fact]
    public async Task CatalogRebuildsWhenConnectorImageChanges()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var grpcService = new CapturingWorkspaceGrpcService { SucceedListTools = true };
        AgentDefinition agent = CreateBoundContainerAgent();
        PlatformConfiguration firstConfig = CreateContainerPlatform(agent, "ghcr.io/example/container-mcp:v1");
        AgentToolCatalog firstCatalog = CreateCatalog(testStore, firstConfig, new CapturingMcpHttpClientFactory(), grpcService, new CountingSkillPackageService());
        await firstCatalog.BuildToolsAsync(CreateContainerToolContext(agent), CancellationToken.None);

        PlatformConfiguration secondConfig = CreateContainerPlatform(agent, "ghcr.io/example/container-mcp:v2");
        AgentToolCatalog secondCatalog = CreateCatalog(testStore, secondConfig, new CapturingMcpHttpClientFactory(), grpcService, new CountingSkillPackageService());
        await secondCatalog.BuildToolsAsync(CreateContainerToolContext(agent), CancellationToken.None);

        Assert.Equal(2, grpcService.ListToolsCallCount);
    }

    [Fact]
    public async Task CatalogRebuildsWhenBindingChanges()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var grpcService = new CapturingWorkspaceGrpcService { SucceedListTools = true };
        AgentDefinition firstAgent = CreateBoundContainerAgent("container-mcp");
        PlatformConfiguration config = new()
        {
            McpServers =
            [
                new McpServerDefinition
                {
                    Id = "container-mcp",
                    Name = "container",
                    Transport = McpTransport.Container,
                    ContainerImage = "ghcr.io/example/container-mcp:latest",
                    Enabled = true,
                },
                new McpServerDefinition
                {
                    Id = "other-mcp",
                    Name = "other",
                    Transport = McpTransport.Container,
                    ContainerImage = "ghcr.io/example/other:latest",
                    Enabled = true,
                },
            ],
        };
        AgentToolCatalog catalog = CreateCatalog(testStore, config, new CapturingMcpHttpClientFactory(), grpcService, new CountingSkillPackageService());
        await catalog.BuildToolsAsync(CreateContainerToolContext(firstAgent), CancellationToken.None);

        AgentDefinition rebound = CreateBoundContainerAgent("other-mcp");
        await catalog.BuildToolsAsync(CreateContainerToolContext(rebound), CancellationToken.None);

        Assert.Equal(2, grpcService.ListToolsCallCount);
    }

    [Fact]
    public async Task FailedConnectorListIsNotPersisted()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var grpcService = new CapturingWorkspaceGrpcService();
        AgentDefinition agent = CreateBoundContainerAgent();
        PlatformConfiguration config = CreateContainerPlatform(agent, "ghcr.io/example/container-mcp:latest");
        AgentToolCatalog catalog = CreateCatalog(testStore, config, new CapturingMcpHttpClientFactory(), grpcService, new CountingSkillPackageService());
        AgentToolContext context = CreateContainerToolContext(agent);

        _ = await catalog.BuildToolsAsync(context, CancellationToken.None);
        _ = await catalog.BuildToolsAsync(context, CancellationToken.None);

        Assert.Equal(2, grpcService.ListToolsCallCount);
        Assert.Null(testStore.SurfaceStore.TryGet(agent.Id, AgentToolingFingerprint.Compute(agent, config)));
    }

    [Fact]
    public async Task ConcurrentCatalogMissSharesOneContainerListTools()
    {
        using ToolSurfaceTestStore testStore = ToolSurfaceTestStore.Create();
        var grpcService = new CapturingWorkspaceGrpcService { SucceedListTools = true, BlockListTools = true };
        AgentDefinition agent = CreateBoundContainerAgent();
        PlatformConfiguration config = CreateContainerPlatform(agent, "ghcr.io/example/container-mcp:latest");
        AgentToolCatalog catalog = CreateCatalog(testStore, config, new CapturingMcpHttpClientFactory(), grpcService, new CountingSkillPackageService());
        AgentToolContext first = CreateContainerToolContext(agent);
        AgentToolContext second = CreateContainerToolContext(agent);

        Task<IReadOnlyList<Microsoft.Extensions.AI.AIFunction>> firstBuild = catalog.BuildToolsAsync(first, CancellationToken.None).AsTask();
        await grpcService.ListToolsStarted.Task.ConfigureAwait(true);
        Task<IReadOnlyList<Microsoft.Extensions.AI.AIFunction>> secondBuild = catalog.BuildToolsAsync(second, CancellationToken.None).AsTask();

        Assert.Equal(1, grpcService.ListToolsCallCount);
        grpcService.ListToolsRelease.TrySetResult();

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction>[] results = await Task.WhenAll(firstBuild, secondBuild).ConfigureAwait(true);
        Assert.All(results, tools => Assert.Contains(tools, tool => tool.Name == "search"));
        Assert.Equal(1, grpcService.ListToolsCallCount);
    }

    [Fact]
    public async Task ContainerInvokeLoadsHeldRefreshTokenInsteadOfTreatingItAsSignedOut()
    {
        const string refreshKey = "KAUTUKA_M365_REFRESH_TOKEN";
        CredentialResolverService resolver = CreateHeldSecretResolver(refreshKey, "stored-refresh");
        var grpcService = new CapturingWorkspaceGrpcService
        {
            SucceedListTools = true,
            ListedToolName = "mail_list",
            RecordCalls = true,
        };
        var handler = new AccessTokenHandler("mailbox-access");
        using var authorizationClient = new ConnectorAuthorizationClient(handler);
        var oauth = new ConnectorOAuthService(
            new ConnectorAuthCatalog([MailProfile()]),
            new CredentialsHostConnectorRefreshTokenStore(resolver),
            authorizationClient,
            new ConnectorOAuthSessionStore());
        var workspaceTool = new CapturingWorkspaceTool();
        var provider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = "kautuka-m365-mail",
                        Name = "Microsoft 365 mail",
                        Transport = McpTransport.Container,
                        ContainerCommand = "kautuka-m365-mail",
                        EnvKeys = [refreshKey],
                        Enabled = true,
                    },
                ],
            }),
            resolver,
            new CapturingMcpHttpClientFactory(),
            grpcService,
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance,
            oauth);
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition { Enabled = false },
                McpBindings =
                [
                    new AgentMcpBindingDefinition
                    {
                        ConnectorId = "kautuka-m365-mail",
                        Context = "OwnContext",
                    },
                ],
            },
            "agent-alpha",
            "user-1",
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", "user-1", null));

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None).ConfigureAwait(true)];
        Microsoft.Extensions.AI.AIFunction tool = Assert.Single(tools);

        object? result = await tool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(), CancellationToken.None).ConfigureAwait(true);

        Assert.NotNull(result);
        Assert.NotNull(grpcService.LastCall);
        Dictionary<string, string> sent = grpcService.LastCall.Environment.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        Assert.False(sent.ContainsKey(refreshKey));
        Assert.Equal("mailbox-access", sent["KAUTUKA_M365_ACCESS_TOKEN"]);
    }

    [Fact]
    public async Task UserScopedConnectorDoesNotReceiveSystemCredentials()
    {
        var credentials = new ScopeAwareCredentialsClient();
        CredentialResolverService resolver = new(new Lazy<CredentialsService.CredentialsServiceClient>(() => credentials));

        (IReadOnlyList<Microsoft.Extensions.AI.AIFunction> userTools, CapturingWorkspaceGrpcService userGrpc) = await BuildContainerEchoAsync(resolver, "user").ConfigureAwait(true);
        Assert.Empty(userTools);
        Assert.Equal(1, credentials.ResolveCount);
        Assert.False(credentials.LastIncludeSystemScope);
        Assert.Null(userGrpc.LastCall);

        (IReadOnlyList<Microsoft.Extensions.AI.AIFunction> systemTools, CapturingWorkspaceGrpcService systemGrpc) = await BuildContainerEchoAsync(resolver, "system").ConfigureAwait(true);
        Assert.True(credentials.LastIncludeSystemScope);
        Microsoft.Extensions.AI.AIFunction tool = Assert.Single(systemTools);
        _ = await tool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(), CancellationToken.None).ConfigureAwait(true);
        Assert.NotNull(systemGrpc.LastCall);
        Dictionary<string, string> systemEnvironment = systemGrpc.LastCall.Environment.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        Assert.Equal("user-token", systemEnvironment["OWN_TOKEN"]);
        Assert.Equal("system-token", systemEnvironment["MCP_AUTH_TOKEN"]);
    }

    private static async Task<(IReadOnlyList<Microsoft.Extensions.AI.AIFunction> Tools, CapturingWorkspaceGrpcService Grpc)> BuildContainerEchoAsync(
        CredentialResolverService resolver,
        string scope)
    {
        string connectorId = scope + "-echo";
        var grpcService = new CapturingWorkspaceGrpcService
        {
            SucceedListTools = true,
            ListedToolName = "echo",
            RecordCalls = true,
        };
        var provider = new McpAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                McpServers =
                [
                    new McpServerDefinition
                    {
                        Id = connectorId,
                        Name = connectorId,
                        Transport = McpTransport.Container,
                        ContainerCommand = "echo-mcp",
                        EnvKeys = ["OWN_TOKEN", "MCP_AUTH_TOKEN"],
                        Scope = scope,
                        OwnerUserId = string.Equals(scope, "user", StringComparison.Ordinal) ? "user-1" : null,
                        Enabled = true,
                    },
                ],
            }),
            resolver,
            new CapturingMcpHttpClientFactory(),
            grpcService,
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);
        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition { Enabled = false },
                McpBindings =
                [
                    new AgentMcpBindingDefinition
                    {
                        ConnectorId = connectorId,
                        Context = "OwnContext",
                    },
                ],
            },
            "agent-alpha",
            "user-1",
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", "user-1", null));

        IReadOnlyList<Microsoft.Extensions.AI.AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None).ConfigureAwait(true)];
        return (tools, grpcService);
    }

    private static CredentialResolverService CreateHeldSecretResolver(string key, string value)
        => new(new Lazy<CredentialsService.CredentialsServiceClient>(
            () => new HeldSecretCredentialsClient(key, value)));

    private static ConnectorAuthProfile MailProfile() => new()
    {
        Id = "kautuka-m365-mail",
        Name = "Microsoft 365 mail",
        Description = "Read, draft, and send mail.",
        Grant = "authorization_code",
        AuthorizeUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        Scopes = "offline_access Mail.ReadWrite Mail.Send User.Read",
        RedirectUri = "https://auth.kautuka.io/callback",
        StartUrl = "https://auth.kautuka.io/start",
        RefreshCredentialKey = "KAUTUKA_M365_REFRESH_TOKEN",
        AccessTokenEnvironmentKey = "KAUTUKA_M365_ACCESS_TOKEN",
        RevokeUrl = "https://myaccount.microsoft.com/permissions",
        SignInLabel = "Sign in with Microsoft",
        EntryPoint = "kautuka-m365-mail",
        ToolNames = ["mail_list"],
        AskFirst = ["mail_send"],
        ClientId = "11111111-1111-1111-1111-111111111111",
    };

    private sealed class ScopeAwareCredentialsClient : CredentialsService.CredentialsServiceClient
    {
        public int ResolveCount { get; private set; }

        public bool LastIncludeSystemScope { get; private set; }

        public override AsyncUnaryCall<CredentialMapResponse> GetResolvedCredentialsAsync(
            GetResolvedCredentialsRequest request,
            CallOptions options)
        {
            ResolveCount++;
            LastIncludeSystemScope = request.IncludeSystemScope;
            var response = new CredentialMapResponse();
            if (request.RequiredKeys.Contains("OWN_TOKEN"))
            {
                response.Entries["OWN_TOKEN"] = "user-token";
            }

            if (request.IncludeSystemScope && request.RequiredKeys.Contains("MCP_AUTH_TOKEN"))
            {
                response.Entries["MCP_AUTH_TOKEN"] = "system-token";
            }

            return Wrap(response);
        }

        private static AsyncUnaryCall<T> Wrap<T>(T response) where T : class
            => new(
                responseAsync: Task.FromResult(response),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => new Metadata(),
                disposeAction: static () => { });
    }

    private sealed class HeldSecretCredentialsClient(string key, string value) : CredentialsService.CredentialsServiceClient
    {
        public override AsyncUnaryCall<CredentialMapResponse> GetResolvedCredentialsAsync(
            GetResolvedCredentialsRequest request,
            CallOptions options)
        {
            var response = new CredentialMapResponse();
            if (request.IncludeHeldSecrets && request.RequiredKeys.Contains(key))
            {
                response.Entries[key] = value;
            }

            return Wrap(response);
        }

        public override AsyncUnaryCall<CredentialKeyListResponse> ListUserCredentialKeysAsync(
            ListUserCredentialKeysRequest request,
            CallOptions options)
        {
            var response = new CredentialKeyListResponse();
            response.Keys.Add(key);
            return Wrap(response);
        }

        private static AsyncUnaryCall<T> Wrap<T>(T response) where T : class
            => new(
                responseAsync: Task.FromResult(response),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => new Metadata(),
                disposeAction: static () => { });
    }

    private sealed class AccessTokenHandler(string accessToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"access_token":"{{accessToken}}","token_type":"Bearer"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private static AgentToolCatalog CreateCatalog(
        ToolSurfaceTestStore testStore,
        PlatformConfiguration config,
        CapturingMcpHttpClientFactory httpFactory,
        CapturingWorkspaceGrpcService grpcService,
        CountingSkillPackageService inspect)
    {
        var configProvider = new StaticPlatformConfigurationProvider(config);
        var mcp = new McpAgentToolProvider(
            configProvider,
            CreateNoOpCredentialResolver(),
            httpFactory,
            grpcService,
            new ToolExecutionPolicyService(),
            NullLogger<McpAgentToolProvider>.Instance);
        var skill = new SkillAgentToolProvider(
            configProvider,
            inspect,
            CreateNoOpCredentialResolver(),
            new ToolExecutionPolicyService(),
            NullLogger<SkillAgentToolProvider>.Instance);
        return new AgentToolCatalog([mcp, skill], testStore.SurfaceStore, configProvider);
    }

    private static AgentDefinition CreateBoundHttpAgent()
        => new()
        {
            Id = "agent-alpha",
            Name = "Agent Alpha",
            SystemPrompt = "persona prompt",
            Sandbox = new AgentSandboxDefinition { Enabled = true },
            Persona = new System.Text.Json.Nodes.JsonObject { ["tone"] = "warm" },
            McpBindings =
            [
                new AgentMcpBindingDefinition
                {
                    ConnectorId = "homeassistant-mcp",
                    Context = "OwnContext",
                },
            ],
            SkillAttachments =
            [
                new AgentSkillAttachmentDefinition
                {
                    SkillId = "roll-dice",
                    Mode = SkillAttachmentMode.Trusted,
                },
            ],
        };

    private static AgentDefinition CreateBoundContainerAgent(string connectorId = "container-mcp")
        => new()
        {
            Id = "agent-alpha",
            Name = "Agent Alpha",
            Sandbox = new AgentSandboxDefinition { Enabled = false },
            McpBindings =
            [
                new AgentMcpBindingDefinition
                {
                    ConnectorId = connectorId,
                    Context = "OwnContext",
                },
            ],
        };

    private static PlatformConfiguration CreateHttpPlatform(AgentDefinition agent)
        => new()
        {
            Agents = [agent],
            McpServers =
            [
                new McpServerDefinition
                {
                    Id = "homeassistant-mcp",
                    Name = "Home Assistant",
                    Transport = McpTransport.Http,
                    HttpUrl = new Uri("https://ha.example.com/api/mcp", UriKind.Absolute),
                    Enabled = true,
                    ToolCount = 3,
                    ToolNames = ["ha_status", "ha_call"],
                    UpdatedAt = "2026-08-01T00:00:00.0000000+00:00",
                },
            ],
            Skills =
            [
                new SkillDefinition
                {
                    SkillId = "roll-dice",
                    Name = "Roll Dice",
                    PackageHash = "hash-roll-dice",
                    Status = "ready",
                    HasScripts = true,
                },
            ],
        };

    private static PlatformConfiguration CreateContainerPlatform(AgentDefinition agent, string image)
        => new()
        {
            Agents = [agent],
            McpServers =
            [
                new McpServerDefinition
                {
                    Id = agent.McpBindings[0].ConnectorId,
                    Name = "container",
                    Transport = McpTransport.Container,
                    ContainerImage = image,
                    Enabled = true,
                },
            ],
        };

    private static AgentToolContext CreateHttpToolContext(AgentDefinition agent)
    {
        var workspaceTool = new CapturingWorkspaceTool();
        return new AgentToolContext(
            agent,
            agent.Id,
            UserId: null,
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, agent.Id, null, null),
            EffectiveSkillManifest:
            [
                new EffectiveSkillManifestEntry
                {
                    SkillId = "roll-dice",
                    Mode = SkillAttachmentMode.Trusted,
                    PackageHash = "hash-roll-dice",
                    HasScripts = true,
                    Name = "Roll Dice",
                },
            ]);
    }

    private static AgentToolContext CreateContainerToolContext(AgentDefinition agent, string? userId = null)
    {
        var workspaceTool = new CapturingWorkspaceTool();
        return new AgentToolContext(
            agent,
            agent.Id,
            userId,
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, agent.Id, userId, null));
    }

    private static AgentToolContext CreateHttpConnectorContext(string connectorId, string userId)
    {
        var workspaceTool = new CapturingWorkspaceTool();
        var agent = new AgentDefinition
        {
            Id = "agent-alpha",
            Name = "Agent Alpha",
            Sandbox = new AgentSandboxDefinition { Enabled = false },
            McpBindings =
            [
                new AgentMcpBindingDefinition
                {
                    ConnectorId = connectorId,
                    Context = "OwnContext",
                },
            ],
        };
        return new AgentToolContext(
            agent,
            agent.Id,
            userId,
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, agent.Id, userId, null));
    }

    private static CredentialResolverService CreateNoOpCredentialResolver()
    {
        // Tests do not invoke credential resolution (McpServerDefinitions have no EnvKeys).
        // Create a resolver backed by a disconnected channel — never actually called.
        GrpcChannel channel = GrpcChannel.ForAddress("http://127.0.0.1:1");
        return new CredentialResolverService(new Lazy<CredentialsService.CredentialsServiceClient>(
            new CredentialsService.CredentialsServiceClient(channel)));
    }

    private sealed class StaticPlatformConfigurationProvider(PlatformConfiguration config) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => config;
    }

    private sealed class CapturingMcpHttpClientFactory : IMcpHttpClientFactory
    {
        public bool WasCalled { get; private set; }

        public Task<McpClient> CreateAsync(
            HttpClientTransportOptions transportOptions,
            bool allowPrivateDestination,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("Synthetic MCP HTTP factory failure for transport-path verification.");
        }
    }

    private sealed class CapturingAsyncDisposable : IAsyncDisposable
    {
        public bool DisposeAsyncCalled { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCalled = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingWorkspaceTool : IWorkspaceRuntimeTool
    {
        public int SpawnAgentSandboxCallCount { get; private set; }

        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null)
            => new(
                ScopeType: "agent",
                ScopeId: agentId,
                UserId: userId,
                HostWorkspacePath: "D:/temp/agent",
                ContainerWorkspacePath: "/workspace",
                RuntimeHostWorkspacePath: "D:/temp/agent",
                Image: "kautuka-base:latest",
                DefaultTimeoutSeconds: 30,
                BaseCapabilities: [],
                ContainerEnvironment: new Dictionary<string, string>());

        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null)
            => DescribeAgentWorkspace(agentId, userId);

        public bool DeleteAgentWorkspace(string agentId)
            => true;

        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
        {
            SpawnAgentSandboxCallCount++;
            return Task.FromResult(new WorkspaceSandboxSession("container", true, "agent", agentId, userId));
        }

        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceSandboxSession("container", false, "agent", agentId, userId));

        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingWorkspaceGrpcService : IManagerGrpcService
    {
        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CapturingWorkspaceGrpcService : IManagerGrpcService
    {
        public bool ListToolsWasCalled { get; private set; }
        public int ListToolsCallCount { get; private set; }
        public bool SucceedListTools { get; init; }
        public string ListedToolName { get; set; } = "search";
        public bool RecordCalls { get; init; }
        public WorkspaceMcpContainerCallRequest? LastCall { get; private set; }
        public bool BlockListTools { get; init; }
        public TaskCompletionSource ListToolsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ListToolsRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkspaceMcpContainerCallRequest? LastListToolsRequest { get; private set; }

        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
        {
            ListToolsWasCalled = true;
            ListToolsCallCount++;
            LastListToolsRequest = request;
            if (BlockListTools)
            {
                ListToolsStarted.TrySetResult();
                await ListToolsRelease.Task.ConfigureAwait(false);
            }

            if (!SucceedListTools)
            {
                throw new NotSupportedException();
            }

            var response = new WorkspaceMcpContainerListToolsResponse { Ok = true };
            response.Tools.Add(new WorkspaceMcpToolMessage
            {
                Name = ListedToolName,
                Description = "Search",
                JsonSchema = """{"type":"object"}""",
            });
            return response;
        }

        public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
        {
            if (!RecordCalls)
            {
                throw new NotSupportedException();
            }

            LastCall = request;
            return Task.FromResult(new WorkspaceMcpContainerCallResponse
            {
                Ok = true,
                ResultJson = "{}",
            });
        }

        public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CountingSkillPackageService : ISkillPackageService
    {
        public int InspectCount { get; private set; }

        public Task<SkillPackageImportOutcome> ValidateImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SkillPackageImportOutcome> ImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public SkillPackageInspection Inspect(SkillDefinition skill)
        {
            InspectCount++;
            return new SkillPackageInspection(skill, string.Empty, [], [], []);
        }

        public string ReadResourceText(SkillDefinition skill, string resourcePath)
            => throw new NotSupportedException();

        public string StagePackageForWorkspace(SkillDefinition skill, string workspaceHostPath, string containerWorkspacePath)
            => throw new NotSupportedException();

        public string ConfinePackagePath(string skillId, string? requestedPath, string? existingPath)
            => string.IsNullOrWhiteSpace(requestedPath) ? existingPath ?? string.Empty : requestedPath;

        public void DeletePackage(string skillId, string packagePath)
        {
        }
    }

    private sealed class ToolSurfaceTestStore : IDisposable
    {
        private readonly string directoryPath;

        private ToolSurfaceTestStore(string directoryPath, IAgentToolSurfaceStore surfaceStore)
        {
            this.directoryPath = directoryPath;
            SurfaceStore = surfaceStore;
        }

        public IAgentToolSurfaceStore SurfaceStore { get; }

        public static ToolSurfaceTestStore Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), "kautuka-tool-surface-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = directoryPath,
                })
                .Build();
            var database = new SqliteAgentStoreDatabase(configuration);
            var definitions = new SqliteAgentDefinitionStore(configuration);
            return new ToolSurfaceTestStore(directoryPath, new SqliteAgentToolSurfaceStore(database, definitions));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
