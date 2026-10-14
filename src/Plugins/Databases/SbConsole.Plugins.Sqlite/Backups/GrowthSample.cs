namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>One file-size sample for the Overview's "Growth — 30 days" card (design spec §11.8).</summary>
public sealed record GrowthSample(DateTimeOffset At, long Bytes);
