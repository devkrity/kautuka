namespace Kautuka.Control.Core.Api;

/// <summary>Strongly-typed API error — no exceptions for expected outcomes.</summary>
public abstract record ApiError
{
    protected ApiError() { }

    public override string ToString() => this switch
    {
        UnauthorizedApiError => "Unauthorized (401) — please log in",
        NotFoundApiError => "Not found (404)",
        TransportApiError t => $"Network error: {t.Inner.Message}",
        UnexpectedApiError(var s, var b) => $"Unexpected {s}: {b}",
        _ => base.ToString()!,
    };
}

internal sealed record UnauthorizedApiError() : ApiError;
internal sealed record NotFoundApiError() : ApiError;
internal sealed record TransportApiError(Exception Inner) : ApiError;
internal sealed record UnexpectedApiError(int StatusCode, string Body) : ApiError;

/// <summary>Result of an API call — either a value or an ApiError.</summary>
public readonly record struct ApiResult<T>
{
    private readonly T? value;
    private readonly ApiError? error;
    private readonly bool isOk;

    internal ApiResult(T value)
    {
        this.value = value;
        error = null;
        isOk = true;
    }

    internal ApiResult(ApiError error)
    {
        value = default;
        this.error = error;
        isOk = false;
    }

    public bool IsOk => isOk;
    public T Value => isOk ? value! : throw new InvalidOperationException("ApiResult is error");
    public ApiError Error => !isOk ? error! : throw new InvalidOperationException("ApiResult is ok");

    public TOut Match<TOut>(Func<T, TOut> ok, Func<ApiError, TOut> err)
    {
        ArgumentNullException.ThrowIfNull(ok);
        ArgumentNullException.ThrowIfNull(err);
        return isOk ? ok(value!) : err(error!);
    }
}

public static class ApiResults
{
    public static ApiResult<T> Ok<T>(T value) => new(value);

    public static ApiResult<T> Err<T>(ApiError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(error);
    }
}
