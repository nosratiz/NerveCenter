namespace SbConsole.Core.Data.Entities;

public sealed class PluginDocument
{
    public required string PluginId { get; set; }
    public required string Key { get; set; }
    public required string Json { get; set; }
}
