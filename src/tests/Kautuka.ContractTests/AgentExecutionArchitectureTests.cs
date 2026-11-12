using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.ContractTests;

public sealed class AgentExecutionArchitectureTests
{
    [Fact]
    public void CanonicalExecutionEventConstructionHasFocusedOwnerAndRatchetedSize()
    {
        Type writer = typeof(AgentExecutionEventWriter);
        Assert.True(writer.IsSealed);
        Assert.NotNull(writer.GetMethod(nameof(AgentExecutionEventWriter.WriteAsync)));
        Assert.NotNull(writer.GetMethod(nameof(AgentExecutionEventWriter.WriteRuntimeAsync)));

        Type service = typeof(AgentChatService);
        const System.Reflection.BindingFlags nonPublicInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Assert.Null(service.GetMethod("WriteExecutionEventAsync", nonPublicInstance));
        Assert.Null(service.GetMethod("WriteRuntimeEventAsync", nonPublicInstance));

        string executionDirectory = Path.Combine(ResolveGatewayRoot(), "Domain", "Execution");
        AssertFileLineLimit(executionDirectory, "AgentChatService.Streaming.cs", 1640);
        AssertFileLineLimit(executionDirectory, "AgentExecutionEventWriter.cs", 324);
    }

    [Fact]
    public void ToolResultAssetMaterializationHasFocusedOwnerAndRatchetedSize()
    {
        Type processor = typeof(ChatToolResultProcessor);
        const System.Reflection.BindingFlags nonPublicStatic =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

        Assert.True(processor.IsAbstract && processor.IsSealed);
        Assert.NotNull(processor.GetMethod("ProcessToolResult", nonPublicStatic));
        Assert.NotNull(processor.GetMethod("SerializeToolResult", nonPublicStatic));
        Assert.NotNull(processor.GetMethod("ExtractToolResultText", nonPublicStatic));

        Type helpers = typeof(ChatExecutionHelpers);
        Assert.Null(helpers.GetMethod("ProcessToolResult", nonPublicStatic));
        Assert.Null(helpers.GetMethod("SerializeToolResult", nonPublicStatic));
        Assert.Null(helpers.GetMethod("ExtractToolResultText", nonPublicStatic));

        string executionDirectory = Path.Combine(ResolveGatewayRoot(), "Domain", "Execution");
        AssertFileLineLimit(executionDirectory, "ChatExecutionHelpers.cs", 835);
        AssertFileLineLimit(executionDirectory, "ChatToolResultProcessor.cs", 650);
    }

    private static void AssertFileLineLimit(string directory, string fileName, int maximumLines)
    {
        string path = Path.Combine(directory, fileName);
        Assert.True(File.Exists(path), $"Missing execution owner '{fileName}'.");
        int lineCount = File.ReadLines(path).Count();
        Assert.True(lineCount <= maximumLines,
            $"Execution owner '{fileName}' has {lineCount} lines; ratcheted maximum is {maximumLines}.");
    }

    private static string ResolveGatewayRoot()
    {
        foreach (string origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(origin); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "src", "backend", "Kautuka.Gateway");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Kautuka.Gateway source directory.");
    }
}
