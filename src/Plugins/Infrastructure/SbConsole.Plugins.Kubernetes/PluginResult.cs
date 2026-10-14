using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes;

public sealed class PluginResult
{
    private PluginResult(bool isSuccess, string? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public string? Error { get; }

    public static PluginResult Ok() => new(true, null);
    public static PluginResult Fail(string error) => new(false, error);
    public static PluginResult Fail(Exception ex) => new(false, FriendlyKubernetesError.From(ex));
}

public sealed class PluginResult<T>
{
    private PluginResult(bool isSuccess, string? error, T? value)
    {
        IsSuccess = isSuccess;
        Error = error;
        Value = value;
    }

    public bool IsSuccess { get; }
    public string? Error { get; }
    public T? Value { get; }

    public static PluginResult<T> Ok(T value) => new(true, null, value);
    public static PluginResult<T> Fail(string error) => new(false, error, default);
    public static PluginResult<T> Fail(Exception ex) => new(false, FriendlyKubernetesError.From(ex), default);
}
