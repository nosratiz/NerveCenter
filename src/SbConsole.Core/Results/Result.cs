namespace SbConsole.Core.Results;

public sealed class Result
{
    private Result(bool isSuccess, Error? error) => (IsSuccess, Error) = (isSuccess, error);

    public bool IsSuccess { get; }
    public Error? Error { get; }

    public static Result Ok() => new(true, null);
    public static Result Fail(ErrorCategory category, string message) => new(false, new Error(category, message));
}

public sealed class Result<T>
{
    private Result(bool isSuccess, T? value, Error? error) => (IsSuccess, Value, Error) = (isSuccess, value, error);

    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }

    public static Result<T> Ok(T value) => new(true, value, null);
    public static Result<T> Fail(ErrorCategory category, string message) => new(false, default, new Error(category, message));
}
