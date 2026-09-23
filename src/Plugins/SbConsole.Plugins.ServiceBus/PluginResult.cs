using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus;

/// <summary>
/// This plugin's own lightweight result type, mirroring the shape of SbConsole.Core.Results.Result
/// without depending on it — plugins reference only SbConsole.Sdk (docs/design.md §2).
/// </summary>
public sealed class PluginResult
{
    private PluginResult(bool isSuccess, string? error) => (IsSuccess, Error) = (isSuccess, error);
    public bool IsSuccess { get; }
    public string? Error { get; }
    public static PluginResult Ok() => new(true, null);
    public static PluginResult Fail(string error) => new(false, error);

    /// <summary>
    /// Failure from a caught exception. Always prefer this over <c>Fail(ex.Message)</c>: raw SDK
    /// messages reach both a snackbar and (via the audit detail column) the database, so they run
    /// through <see cref="FriendlyError"/> first. The caller still logs the full exception.
    /// </summary>
    public static PluginResult Fail(Exception ex) => new(false, FriendlyError.From(ex));
}

public sealed class PluginResult<T>
{
    private PluginResult(bool isSuccess, T? value, string? error) => (IsSuccess, Value, Error) = (isSuccess, value, error);
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public static PluginResult<T> Ok(T value) => new(true, value, null);
    public static PluginResult<T> Fail(string error) => new(false, default, error);

    /// <inheritdoc cref="PluginResult.Fail(Exception)"/>
    public static PluginResult<T> Fail(Exception ex) => new(false, default, FriendlyError.From(ex));
}
