using SbConsole.Sdk;

namespace SbConsole.Core.Data.Entities;

public sealed class AuditEntry
{
    public long Id { get; set; }

    /// <summary>
    /// The instant the action occurred. Only the UTC instant is preserved through persistence:
    /// any non-zero <see cref="DateTimeOffset.Offset"/> on a written value is lost on read-back —
    /// the value always reads back with an offset of <c>+00:00</c>.
    /// </summary>
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public ActionRisk Risk { get; set; }
    public bool Succeeded { get; set; }
    public string? Detail { get; set; }
}
