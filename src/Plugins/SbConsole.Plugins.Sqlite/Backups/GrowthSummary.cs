using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Backups;

/// <summary>
/// The Overview's "Growth — 30 days" numbers (design spec §11.8): first → last, delta, per-day
/// rate, and days until the volume is full at that rate (only when growing and the volume is
/// known). Collecting while there are fewer than 2 samples or they span under an hour -- then
/// DeltaBytes is 0 and the rates are null.
/// </summary>
public sealed record GrowthSummary(GrowthSample? First, GrowthSample? Last, long DeltaBytes, double? BytesPerDay, double? DaysUntilFull)
{
    public static readonly TimeSpan MinSpan = TimeSpan.FromHours(1);

    public bool Collecting => First is null || Last is null || Last.At - First.At < MinSpan;

    public static GrowthSummary From(IReadOnlyList<GrowthSample> samples, VolumeInfo? volume)
    {
        var ordered = samples.OrderBy(s => s.At).ToList();
        var first = ordered.FirstOrDefault();
        var last = ordered.LastOrDefault();
        if (first is null || last is null || last.At - first.At < MinSpan)
        {
            return new GrowthSummary(first, last, 0, null, null);
        }

        var delta = last.Bytes - first.Bytes;
        var perDay = delta / (last.At - first.At).TotalDays;
        var daysUntilFull = volume is not null && perDay > 0 ? volume.AvailableBytes / perDay : (double?)null;
        return new GrowthSummary(first, last, delta, perDay, daysUntilFull);
    }
}
