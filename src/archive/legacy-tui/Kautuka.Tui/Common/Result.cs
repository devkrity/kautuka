// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Common/Result.cs | Namespace: Kautuka.Tui.Common
namespace Kautuka.Tui.Common;

public sealed record Result(bool IsSuccess, string? ErrorMessage)
{
}

public sealed record Result<T>(bool IsSuccess, T? Value, string? ErrorMessage)
{
}

public static class Results
{
    public static Result Success() => new(true, null);

    public static Result Failure(string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new Result(false, errorMessage);
    }

    public static Result<T> Success<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<T>(true, value, null);
    }

    public static Result<T> Failure<T>(string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new Result<T>(false, default, errorMessage);
    }
}
