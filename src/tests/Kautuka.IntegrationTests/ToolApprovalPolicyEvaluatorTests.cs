namespace Kautuka.IntegrationTests;

public sealed class ToolApprovalPolicyEvaluatorTests
{
    [Fact]
    public void RequiresHumanApprovalFalseForHomeAssistantToolOnMyAssistantCaller()
    {
        const string callerAgentId = "agent-caller";
        const string rollerAgentId = "agent-roller";
        const string homeAssistantAgentId = "agent-home";

        PlatformConfiguration config = new()
        {
            Agents =
            [
                new AgentDefinition
                {
                    Id = callerAgentId,
                    Name = "My Assistant",
                    ApprovalPolicy = new ApprovalPolicy
                    {
                        AgentToolIds = [rollerAgentId, homeAssistantAgentId],
                        ApprovalRequiredAgentToolIds = [rollerAgentId],
                    },
                    ToolExposure = new AgentToolExposureDefinition { ExposedAsTool = false },
                },
                new AgentDefinition
                {
                    Id = rollerAgentId,
                    Name = "Roller",
                    ToolExposure = new AgentToolExposureDefinition { ExposedAsTool = true },
                },
                new AgentDefinition
                {
                    Id = homeAssistantAgentId,
                    Name = "Home Assistant",
                    ToolExposure = new AgentToolExposureDefinition { ExposedAsTool = true },
                },
            ],
        };

        IToolExecutionPolicyService policyService = new ToolExecutionPolicyService();

        Assert.False(ToolApprovalPolicyEvaluator.RequiresHumanApproval(
            policyService,
            config,
            callerAgentId,
            "invoke_agent_home_assistant",
            "{\"message\":\"set patio lights\"}"));

        Assert.True(ToolApprovalPolicyEvaluator.RequiresHumanApproval(
            policyService,
            config,
            callerAgentId,
            "invoke_agent_roller",
            "{\"message\":\"roll a d100\"}"));
    }

    [Fact]
    public void MailSendDoesNotRequireApprovalWhenAgentIsMissing()
    {
        Assert.False(ToolApprovalPolicyEvaluator.RequiresHumanApproval(
            new ToolExecutionPolicyService(),
            new PlatformConfiguration(),
            "missing-agent",
            "mail_send"));
    }

    [Fact]
    public void ShouldDeferToolCallStartForHitlOnlyForWrappedApprovalTools()
    {
        const string callerAgentId = "agent-caller";
        const string rollerAgentId = "agent-roller";
        const string homeAssistantAgentId = "agent-home";

        PlatformConfiguration config = new()
        {
            Agents =
            [
                new AgentDefinition
                {
                    Id = callerAgentId,
                    Name = "My Assistant",
                    ApprovalPolicy = new ApprovalPolicy
                    {
                        AgentToolIds = [rollerAgentId, homeAssistantAgentId],
                        ApprovalRequiredAgentToolIds = [rollerAgentId],
                    },
                    ToolExposure = new AgentToolExposureDefinition { ExposedAsTool = false },
                    SkillAttachments =
                    [
                        new AgentSkillAttachmentDefinition
                        {
                            SkillId = "roll-dice",
                            Mode = SkillAttachmentMode.RequireApproval,
                        },
                    ],
                },
                new AgentDefinition
                {
                    Id = rollerAgentId,
                    Name = "Roller",
                    ToolExposure = new AgentToolExposureDefinition { ExposedAsTool = true },
                    SkillAttachments =
                    [
                        new AgentSkillAttachmentDefinition
                        {
                            SkillId = "roll-dice",
                            Mode = SkillAttachmentMode.RequireApproval,
                        },
                    ],
                },
                new AgentDefinition
                {
                    Id = homeAssistantAgentId,
                    Name = "Home Assistant",
                    ToolExposure = new AgentToolExposureDefinition { ExposedAsTool = true },
                },
            ],
        };

        IToolExecutionPolicyService policyService = new ToolExecutionPolicyService();

        Assert.True(ToolApprovalPolicyEvaluator.ShouldDeferToolCallStartForHitl(
            policyService,
            config,
            callerAgentId,
            "invoke_agent_roller",
            "{\"message\":\"roll a d100\"}"));

        Assert.False(ToolApprovalPolicyEvaluator.ShouldDeferToolCallStartForHitl(
            policyService,
            config,
            callerAgentId,
            "invoke_agent_home_assistant",
            "{\"message\":\"set patio lights\"}"));

        // load_skill is never ApprovalRequiredAIFunction-wrapped — must not defer.
        Assert.False(ToolApprovalPolicyEvaluator.ShouldDeferToolCallStartForHitl(
            policyService,
            config,
            rollerAgentId,
            "load_skill",
            "{\"skillId\":\"roll-dice\"}"));

        Assert.True(ToolApprovalPolicyEvaluator.ShouldDeferToolCallStartForHitl(
            policyService,
            config,
            rollerAgentId,
            "run_skill_script",
            "{\"skillId\":\"roll-dice\",\"namedArguments\":{\"sides\":100}}"));

        // MCP/connector tools without server context: do not defer (bypass path has no TARC).
        Assert.False(ToolApprovalPolicyEvaluator.ShouldDeferToolCallStartForHitl(
            policyService,
            config,
            homeAssistantAgentId,
            "HassTurnOn",
            "{\"name\":\"Patio\"}"));
    }

    [Fact]
    public void ShouldSurfacePendingApprovalIncludesOuterSurfacedInnerApprovals()
    {
        const string callerAgentId = "agent-caller";
        const string rollerAgentId = "agent-roller";
        const string outerSessionId = "session-outer";
        const string innerSessionId = "session-inner";

        PlatformConfiguration config = new()
        {
            Agents =
            [
                new AgentDefinition
                {
                    Id = callerAgentId,
                    Name = "My Assistant",
                    ApprovalPolicy = new ApprovalPolicy
                    {
                        AgentToolIds = [rollerAgentId],
                        ApprovalRequiredAgentToolIds = [rollerAgentId],
                    },
                },
                new AgentDefinition
                {
                    Id = rollerAgentId,
                    Name = "Roller",
                    SkillAttachments =
                    [
                        new AgentSkillAttachmentDefinition
                        {
                            SkillId = "roll-dice",
                            Mode = SkillAttachmentMode.RequireApproval,
                        },
                    ],
                },
            ],
        };

        IToolExecutionPolicyService policyService = new ToolExecutionPolicyService();
        GatewayToolApprovalRequest innerApproval = new(
            Id: "req-inner",
            SessionId: innerSessionId,
            RunId: "run-inner",
            CheckpointId: null,
            AgentId: rollerAgentId,
            UserId: "user-1",
            ToolCallId: "tool-inner",
            ToolName: "run_skill_script",
            ToolType: "skill",
            McpServerId: null,
            ArgumentsJson: "{\"skillId\":\"roll-dice\",\"namedArguments\":{\"sides\":100}}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null,
            OuterSessionId: outerSessionId,
            OuterAgentId: callerAgentId);

        Assert.True(ToolApprovalPolicyEvaluator.ShouldSurfacePendingApproval(
            policyService,
            config,
            outerSessionId,
            innerApproval));
    }
}
