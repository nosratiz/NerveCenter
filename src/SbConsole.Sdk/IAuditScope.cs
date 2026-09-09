namespace SbConsole.Sdk;

/// <summary>Plugins report what they did; the host writes the audit row.</summary>
public interface IAuditScope
{
    Task RecordAsync(string action, string target, ActionRisk risk, bool succeeded, string? detail = null, CancellationToken ct = default);
}
