namespace Kautuka.Control.Core.Auth;

public sealed record SessionToken
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
}

public sealed record LoginRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}

public sealed record LoginResponse
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
}
