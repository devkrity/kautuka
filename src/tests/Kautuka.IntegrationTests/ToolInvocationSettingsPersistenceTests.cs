using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.IntegrationTests;

public sealed class ToolInvocationSettingsPersistenceTests
{
    [Fact]
    public void MapperOmitsNullOverridesAndPersistsExplicitFalse()
    {
        JsonObject omitted = GatewayAgentSettingsMapper.ToJsonObject(new GatewayAgentRuntimePayload());
        Assert.False(omitted.ContainsKey("allowMultipleToolCalls"));
        Assert.False(omitted.ContainsKey("allowConcurrentInvocation"));
        Assert.False(omitted.ContainsKey("maxParallelTools"));

        JsonObject explicitFalse = GatewayAgentSettingsMapper.ToJsonObject(new GatewayAgentRuntimePayload
        {
            AllowMultipleToolCalls = false,
            AllowConcurrentInvocation = false,
            MaxParallelTools = 0,
        });

        Assert.False(explicitFalse["allowMultipleToolCalls"]!.GetValue<bool>());
        Assert.False(explicitFalse["allowConcurrentInvocation"]!.GetValue<bool>());
        Assert.Equal(0, explicitFalse["maxParallelTools"]!.GetValue<int>());
    }

    [Fact]
    public void MapperReadsCamelAndSnakeCaseAndClampsCap()
    {
        var existing = new JsonObject
        {
            ["allow_multiple_tool_calls"] = false,
            ["allowConcurrentInvocation"] = true,
            ["max_parallel_tools"] = RuntimeSettingKeys.MaxParallelToolsCap + 10,
        };

        GatewayAgentRuntimePayload payload = GatewayAgentSettingsMapper.NormalizeRuntime(null, existing);
        Assert.False(payload.AllowMultipleToolCalls);
        Assert.True(payload.AllowConcurrentInvocation);
        Assert.Equal(RuntimeSettingKeys.MaxParallelToolsCap, payload.MaxParallelTools);
    }

    [Fact]
    public void AgentDefinitionSerializerRoundTripsToolInvocationOverrides()
    {
        var definition = new AgentDefinition
        {
            Id = "agent-1",
            Name = "Office",
            ModelId = "model-1",
            Runtime = new AgentRuntimeDefinition
            {
                AllowMultipleToolCalls = true,
                AllowConcurrentInvocation = true,
                MaxParallelTools = 5,
            },
        };

        AgentDefinitionSerializer.SerializedAgentDefinition serialized = AgentDefinitionSerializer.Serialize(definition);
        AgentDefinition roundTripped = AgentDefinitionSerializer.Deserialize(serialized);

        Assert.True(roundTripped.Runtime.AllowMultipleToolCalls);
        Assert.True(roundTripped.Runtime.AllowConcurrentInvocation);
        Assert.Equal(5, roundTripped.Runtime.MaxParallelTools);
    }

    [Fact]
    public void OldEffectiveToolingSnapshotDefaultsToSerialUnlimited()
    {
        const string legacyJson = """
            {"toolNames":["tool-a"],"toolMode":"Auto","allowMultipleToolCalls":true}
            """;

        EffectiveToolingSnapshot? snapshot = JsonSerializer.Deserialize(
            legacyJson,
            KautukaJsonContext.Default.EffectiveToolingSnapshot);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.AllowMultipleToolCalls);
        Assert.False(snapshot.AllowConcurrentInvocation);
        Assert.Equal(0, snapshot.MaxParallelTools);

        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.FromSnapshot(
            snapshot.AllowMultipleToolCalls,
            snapshot.AllowConcurrentInvocation,
            snapshot.MaxParallelTools);
        Assert.True(resolved.AllowMultipleToolCalls);
        Assert.False(resolved.AllowConcurrentInvocation);
        Assert.Null(resolved.ConcurrencyLimit);
    }
}
