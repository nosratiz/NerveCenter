using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Audit;

public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}
