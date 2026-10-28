using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kautuka.Contracts;

public sealed record AuthLoginRequest(string Username, string Password);

public sealed record AuthSetupRequest(string Username, string Password, string? SetupToken = null);

public sealed record AuthSetupStatusResponse(bool NeedsSetup);

public sealed record AuthErrorResponse(string Detail);

public sealed record GatewayUserPayload(
    string Id,
    string Username,
    string? Email,
    string? DisplayName,
    string Role,
    bool IsActive,
    bool CanImportConnectors,
    bool CanImportSkills,
    bool HasPassword = true,
    IReadOnlyList<string>? ExternalProviders = null);

public sealed record AuthLoginResponse(string AccessToken, string TokenType, int ExpiresInSeconds, GatewayUserPayload User);

public sealed record AuthRefreshResponse(string AccessToken, string TokenType, int ExpiresInSeconds);

public sealed record AuthLogoutResponse(bool Success);

public sealed record GatewayErrorResponse(string Detail);

public sealed record GatewayBooleanSuccessResponse(bool Success);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AuthLoginRequest))]
[JsonSerializable(typeof(AuthSetupRequest))]
[JsonSerializable(typeof(AuthSetupStatusResponse))]
[JsonSerializable(typeof(AuthErrorResponse))]
[JsonSerializable(typeof(GatewayUserPayload))]
[JsonSerializable(typeof(AuthLoginResponse))]
[JsonSerializable(typeof(AuthRefreshResponse))]
[JsonSerializable(typeof(AuthLogoutResponse))]
[JsonSerializable(typeof(GatewayErrorResponse))]
[JsonSerializable(typeof(GatewayBooleanSuccessResponse))]
public sealed partial class ContractsJsonContext : JsonSerializerContext
{
}
