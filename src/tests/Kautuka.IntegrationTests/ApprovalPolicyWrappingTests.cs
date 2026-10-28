using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

/// <summary>
/// Unit tests for McpAgentToolProvider.ApplyApprovalPolicy and McpAgentToolProvider.MatchesGlob.
/// These test the pure decision logic that determines whether a tool is wrapped with
/// ApprovalRequiredAIFunction, left unwrapped, or removed entirely (disabled).
/// </summary>
public sealed class ApprovalPolicyWrappingTests
{
    // ── Helper factories ─────────────────────────────────────────────────────

    private static AIFunction Tool(string name)
        => AIFunctionFactory.Create(
            ([System.ComponentModel.Description("x")] string x) => x,
            name,
            $"Tool {name}",
            serializerOptions: null);

    private static (string ServerId, IReadOnlyList<AIFunction>? Tools) Server(string id, params AIFunction[] tools)
        => (id, tools);

    private static bool IsWrapped(AIFunction f) => f is ApprovalRequiredAIFunction;

    // ── Fast path ────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyPolicyReturnsAllToolsUnwrapped()
    {
        var tools = new[] { Tool("list_files"), Tool("read_file") };
        var server = Server("fs", tools);
        var policy = new ApprovalPolicy();

        var result = McpAgentToolProvider.ApplyApprovalPolicy([server], policy).ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, f => Assert.False(IsWrapped(f)));
    }

    [Fact]
    public void EmptyPolicyLeavesMailSendUnwrapped()
    {
        var policy = new ApprovalPolicy();
        var servers = new[]
        {
            Server("kautuka-m365-mail", Tool("mail_list"), Tool("mail_send")),
            Server("other-mail", Tool("mail_send")),
        };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.Equal(3, result.Count);
        Assert.All(result, f => Assert.False(IsWrapped(f)));
    }

    [Fact]
    public void DisabledMicrosoftMailSendStaysExcluded()
    {
        var policy = new ApprovalPolicy
        {
            DisabledMcpFunctions = new Dictionary<string, IReadOnlyList<string>>
            {
                ["kautuka-m365-mail"] = ["mail_send"],
            },
        };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(
            [Server("kautuka-m365-mail", Tool("mail_list"), Tool("mail_send"))],
            policy).ToList();

        Assert.Single(result);
        Assert.Equal("mail_list", result[0].Name);
        Assert.False(IsWrapped(result[0]));
    }

    [Fact]
    public void EmptyPolicyNullToolsListReturnsEmpty()
    {
        (string, IReadOnlyList<AIFunction>?) server = ("fs", null);
        var result = McpAgentToolProvider.ApplyApprovalPolicy([server], new ApprovalPolicy()).ToList();
        Assert.Empty(result);
    }

    // ── RequireMcpIds (server-level approval) ────────────────────────────────

    [Fact]
    public void RequireMcpIdsWrapsAllToolsOnNamedServerLeavesOthersUnwrapped()
    {
        var policy = new ApprovalPolicy { RequireMcpIds = ["fs"] };
        var servers = new[]
        {
            Server("fs",     Tool("list_files"), Tool("read_file")),
            Server("sqlite", Tool("query_db")),
        };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.Equal(3, result.Count);
        Assert.True(IsWrapped(result.Single(f => f.Name == "list_files")));
        Assert.True(IsWrapped(result.Single(f => f.Name == "read_file")));
        Assert.False(IsWrapped(result.Single(f => f.Name == "query_db")));
    }

    [Fact]
    public void RequireMcpIdsMatchingIsCaseInsensitive()
    {
        var policy = new ApprovalPolicy { RequireMcpIds = ["FileSystem-MCP"] };
        var servers = new[] { Server("filesystem-mcp", Tool("read_file")) };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.Single(result);
        Assert.True(IsWrapped(result[0]));
    }

    [Fact]
    public void RequireMcpIdsServerNotInListNoWrapping()
    {
        var policy = new ApprovalPolicy { RequireMcpIds = ["other-server"] };
        var servers = new[] { Server("fs", Tool("read_file")) };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.Single(result);
        Assert.False(IsWrapped(result[0]));
    }

    // ── RequireMcpFunctionFilters (per-function approval) ────────────────────

    [Fact]
    public void RequireMcpFunctionFiltersWithServerInRequireMcpIdsWrapsOnlyMatchingFunctions()
    {
        // "fs" is in RequireMcpIds AND has a function filter → only matching functions need approval.
        var policy = new ApprovalPolicy
        {
            RequireMcpIds = ["fs"],
            RequireMcpFunctionFilters = new Dictionary<string, IReadOnlyList<string>>
            {
                ["fs"] = ["write_file", "delete_file"],
            },
        };
        var servers = new[] { Server("fs", Tool("read_file"), Tool("write_file"), Tool("delete_file")) };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.Equal(3, result.Count);
        Assert.False(IsWrapped(result.Single(f => f.Name == "read_file")));
        Assert.True(IsWrapped(result.Single(f => f.Name == "write_file")));
        Assert.True(IsWrapped(result.Single(f => f.Name == "delete_file")));
    }

    [Fact]
    public void RequireMcpFunctionFiltersAloneWithoutServerInRequireMcpIdsStillWrapsMatchingTools()
    {
        // RequireMcpFunctionFilters applies wrapping directly — the server does NOT need to be in
        // RequireMcpIds. RequireMcpIds controls "wrap all tools on this server"; the function filter
        // controls "wrap these specific tools on this server". They work independently.
        var policy = new ApprovalPolicy
        {
            RequireMcpFunctionFilters = new Dictionary<string, IReadOnlyList<string>>
            {
                ["fs"] = ["write_file"],
            },
        };
        var servers = new[] { Server("fs", Tool("read_file"), Tool("write_file")) };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.Equal(2, result.Count);
        Assert.False(IsWrapped(result.Single(f => f.Name == "read_file")));
        Assert.True(IsWrapped(result.Single(f => f.Name == "write_file")));
    }

    [Fact]
    public void RequireMcpFunctionFiltersGlobPatternPrefixWildcardMatchesTools()
    {
        var policy = new ApprovalPolicy
        {
            RequireMcpIds = ["fs"],
            RequireMcpFunctionFilters = new Dictionary<string, IReadOnlyList<string>>
            {
                ["fs"] = ["write_*"],
            },
        };
        var servers = new[] { Server("fs", Tool("write_file"), Tool("write_dir"), Tool("read_file")) };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.True(IsWrapped(result.Single(f => f.Name == "write_file")));
        Assert.True(IsWrapped(result.Single(f => f.Name == "write_dir")));
        Assert.False(IsWrapped(result.Single(f => f.Name == "read_file")));
    }

    // ── DisabledMcpFunctions ─────────────────────────────────────────────────

    [Fact]
    public void DisabledMcpFunctionsRemovesToolFromOutput()
    {
        var policy = new ApprovalPolicy
        {
            DisabledMcpFunctions = new Dictionary<string, IReadOnlyList<string>>
            {
                ["fs"] = ["delete_file"],
            },
        };
        var servers = new[] { Server("fs", Tool("read_file"), Tool("delete_file"), Tool("write_file")) };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, f => f.Name == "delete_file");
        Assert.All(result, f => Assert.False(IsWrapped(f)));
    }

    [Fact]
    public void DisabledMcpFunctionsDisabledAndRequireApprovalDisabledTakesPrecedence()
    {
        // A tool in DisabledMcpFunctions is removed even if the server is also in RequireMcpIds.
        var policy = new ApprovalPolicy
        {
            RequireMcpIds = ["fs"],
            DisabledMcpFunctions = new Dictionary<string, IReadOnlyList<string>>
            {
                ["fs"] = ["rm_rf"],
            },
        };
        var servers = new[] { Server("fs", Tool("read_file"), Tool("rm_rf")) };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.Single(result);
        Assert.Equal("read_file", result[0].Name);
        Assert.True(IsWrapped(result[0])); // read_file is still wrapped because server is in RequireMcpIds
    }

    [Fact]
    public void DisabledMcpFunctionsOtherServerToolsNotAffected()
    {
        var policy = new ApprovalPolicy
        {
            DisabledMcpFunctions = new Dictionary<string, IReadOnlyList<string>>
            {
                ["bad-server"] = ["dangerous_tool"],
            },
        };
        var servers = new[]
        {
            Server("bad-server", Tool("safe_tool"), Tool("dangerous_tool")),
            Server("good-server", Tool("dangerous_tool")), // same name, different server — not disabled
        };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        Assert.Equal(2, result.Count); // bad-server/safe_tool + good-server/dangerous_tool
    }

    // ── MatchesGlob ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("read_file", "read_file", true)]
    [InlineData("read_file", "READ_FILE", true)]  // case-insensitive
    [InlineData("read_file", "write_file", false)]
    [InlineData("read_file", "", false)]  // empty pattern
    [InlineData("anything", "*", true)]   // wildcard-only
    [InlineData("write_file", "write_*", true)]   // prefix*
    [InlineData("write_dir", "write_*", true)]
    [InlineData("read_file", "write_*", false)]
    [InlineData("write_file", "*_file", true)]   // *suffix
    [InlineData("read_file", "*_file", true)]
    [InlineData("write_dir", "*_file", false)]
    [InlineData("write_file", "write*file", true)]   // prefix*suffix
    [InlineData("writefile", "write*file", true)]   // zero chars between
    [InlineData("write_x_file", "write*file", true)]
    [InlineData("read_file", "write*file", false)]
    [InlineData("write_tool", "write_*", true)]
    [InlineData("write_", "write_*", true)]   // trailing exactly on wildcard
    public void MatchesGlobPatterns(string toolName, string pattern, bool expected)
    {
        Assert.Equal(expected, McpAgentToolProvider.MatchesGlob(toolName, pattern));
    }

    // ── Multi-server edge cases ──────────────────────────────────────────────

    [Fact]
    public void MultipleServersPolicyAppliesToCorrectServerNoMixup()
    {
        var policy = new ApprovalPolicy
        {
            RequireMcpIds = ["server-a"],
            DisabledMcpFunctions = new Dictionary<string, IReadOnlyList<string>>
            {
                ["server-b"] = ["secret_tool"],
            },
        };
        var servers = new[]
        {
            Server("server-a", Tool("tool_1"), Tool("tool_2")),
            Server("server-b", Tool("tool_1"), Tool("secret_tool")),
        };

        var result = McpAgentToolProvider.ApplyApprovalPolicy(servers, policy).ToList();

        // server-a: both tools wrapped (full server approval)
        // server-b: tool_1 unwrapped, secret_tool removed
        Assert.Equal(3, result.Count);
        var serverATools = result.Where(f => f.Name == "tool_1" || f.Name == "tool_2").ToList();
        // Note: both server-a and server-b have "tool_1" — result has two "tool_1"
        // One from server-a (wrapped), one from server-b (unwrapped)
        var wrappedTool1 = result.Where(f => f.Name == "tool_1" && IsWrapped(f)).ToList();
        var unwrappedTool1 = result.Where(f => f.Name == "tool_1" && !IsWrapped(f)).ToList();
        Assert.Single(wrappedTool1);   // from server-a
        Assert.Single(unwrappedTool1); // from server-b
        Assert.DoesNotContain(result, f => f.Name == "secret_tool");
    }
}
