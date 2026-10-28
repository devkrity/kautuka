using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace Kautuka.Gateway.Infrastructure.AI;

/// <summary>
/// Fallback source-generated STJ contracts for OllamaSharp under Native AOT.
/// Prefer OllamaSharp's internal <c>JsonSourceGenerationContext</c> (resolved at runtime)
/// because it includes internal <c>OllamaFunctionResultContent</c> used for tool results.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(DeleteModelRequest))]
[JsonSerializable(typeof(ListModelsResponse))]
[JsonSerializable(typeof(ListRunningModelsResponse))]
[JsonSerializable(typeof(EmbedRequest))]
[JsonSerializable(typeof(EmbedResponse))]
[JsonSerializable(typeof(ShowModelRequest))]
[JsonSerializable(typeof(ShowModelResponse))]
[JsonSerializable(typeof(CreateModelRequest))]
[JsonSerializable(typeof(CreateModelResponse))]
[JsonSerializable(typeof(PullModelRequest))]
[JsonSerializable(typeof(PullModelResponse))]
[JsonSerializable(typeof(PushModelRequest))]
[JsonSerializable(typeof(PushModelResponse))]
[JsonSerializable(typeof(GenerateDoneResponseStream))]
[JsonSerializable(typeof(GenerateResponseStream))]
[JsonSerializable(typeof(GenerateRequest))]
[JsonSerializable(typeof(Message.ToolCall))]
[JsonSerializable(typeof(Message.Function), TypeInfoPropertyName = "MessageFunction")]
[JsonSerializable(typeof(Tool))]
[JsonSerializable(typeof(Function))]
[JsonSerializable(typeof(Parameters))]
[JsonSerializable(typeof(Property))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatDoneResponseStream))]
[JsonSerializable(typeof(ChatResponseStream))]
[JsonSerializable(typeof(ThinkValue))]
internal sealed partial class OllamaSharpAotJsonContext : JsonSerializerContext;
