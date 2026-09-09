using SbConsole.Sdk;

namespace SbConsole.Core.Data.Entities;

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public ActionRisk Risk { get; set; }
    public bool Succeeded { get; set; }
    public string? Detail { get; set; }
}
