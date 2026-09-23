using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws;

/// <summary>
/// This plugin's own lightweight result type, mirroring SbConsole.Plugins.Kafka.PluginResult
/// without depending on it -- plugins reference only SbConsole.Sdk. Routes exception failures
/// through FriendlyAwsError (not the plain SbConsole.Sdk.FriendlyError) so AWS-specific
/// exceptions get their fixed readable messages.
/// </summary>
public sealed class PluginResult
{
    private PluginResult(bool isSuccess, string? error) => (IsSuccess, Error) = (isSuccess, error);
    public bool IsSuccess { get; }
    public string? Error { get; }
    public static PluginResult Ok() => new(true, null);
    public static PluginResult Fail(string error) => new(false, error);
    public static PluginResult Fail(Exception ex) => new(false, FriendlyAwsError.From(ex));
}

public sealed class PluginResult<T>
{
    private PluginResult(bool isSuccess, T? value, string? error) => (IsSuccess, Value, Error) = (isSuccess, value, error);
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public static PluginResult<T> Ok(T value) => new(true, value, null);
    public static PluginResult<T> Fail(string error) => new(false, default, error);
    public static PluginResult<T> Fail(Exception ex) => new(false, default, FriendlyAwsError.From(ex));
}
