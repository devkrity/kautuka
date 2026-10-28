using System.Text.Json;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed class McpProxyAIFunction(
    string name,
    string description,
    JsonElement jsonSchema,
    Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> invokeAsync) : AIFunction
{
    public override string Name => name;

    public override string Description => description;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties => empty;

    public override JsonElement JsonSchema => jsonSchema;

    public override JsonSerializerOptions JsonSerializerOptions => AIJsonUtilities.DefaultOptions;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => invokeAsync(arguments, cancellationToken);

    private static readonly IReadOnlyDictionary<string, object?> empty = new Dictionary<string, object?>(0, StringComparer.Ordinal);
}
