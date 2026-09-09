namespace SbConsole.Sdk;

/// <summary>Per-plugin persistence. Keys are scoped to the owning plugin; plugins never see the host database.</summary>
public interface IPluginStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
}
