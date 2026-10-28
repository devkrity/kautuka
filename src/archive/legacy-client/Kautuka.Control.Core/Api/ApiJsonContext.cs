using System.Text.Json.Serialization;

using Kautuka.Control.Core.Auth;
using Kautuka.Control.Core.Config;

namespace Kautuka.Control.Core.Api;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentSummary))]
[JsonSerializable(typeof(AgentSummary[]))]
[JsonSerializable(typeof(AgentsResponse))]
[JsonSerializable(typeof(ProviderSummary))]
[JsonSerializable(typeof(ProviderSummary[]))]
[JsonSerializable(typeof(ProvidersResponse))]
[JsonSerializable(typeof(ModelSummary))]
[JsonSerializable(typeof(ModelSummary[]))]
[JsonSerializable(typeof(ModelsResponse))]
[JsonSerializable(typeof(UserSummary))]
[JsonSerializable(typeof(UserSummary[]))]
[JsonSerializable(typeof(UsersResponse))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(SessionSummary[]))]
[JsonSerializable(typeof(SessionsResponse))]
[JsonSerializable(typeof(ApprovalSummary))]
[JsonSerializable(typeof(ApprovalSummary[]))]
[JsonSerializable(typeof(SystemSettings))]
[JsonSerializable(typeof(SystemPayload))]
[JsonSerializable(typeof(RuntimeLimitsPayload))]
[JsonSerializable(typeof(KautukaPayload))]
[JsonSerializable(typeof(WorkspacePayload))]
[JsonSerializable(typeof(AuthMe))]
[JsonSerializable(typeof(SessionToken))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(PortAssignments))]
public sealed partial class ApiJsonContext : JsonSerializerContext;

