using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis;

public sealed class PluginResult
{
    public bool IsSuccess { get; }
    public string? Error { get; }

    private PluginResult(bool isSuccess, string? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static PluginResult Ok() => new(true, null);
    public static PluginResult Fail(string error) => new(false, error);
    public static PluginResult Fail(Exception ex) => new(false, FriendlyRedisError.From(ex));
}

public sealed class PluginResult<T>
{
    public bool IsSuccess { get; }
    public string? Error { get; }
    public T? Value { get; }

    private PluginResult(bool isSuccess, string? error, T? value)
    {
        IsSuccess = isSuccess;
        Error = error;
        Value = value;
    }

    public static PluginResult<T> Ok(T value) => new(true, null, value);
    public static PluginResult<T> Fail(string error) => new(false, error, default);
    public static PluginResult<T> Fail(Exception ex) => new(false, FriendlyRedisError.From(ex), default);
}
