namespace SbConsole.Sdk;

/// <summary>
/// Outcome of IPlugin.TestConnectionAsync — shown in the Connections page's Status column and, for
/// a plugin that populates Identity/Checks, in the connection editor's richer Test-connection panel
/// (design spec docs/superpowers/specs/2026-09-22-connections-page-redesign-design.md §3). Identity
/// and Checks are optional: a plugin that never sets them (Service Bus, Kafka today) renders exactly
/// the plain single-line message it always has.
///
/// Three outcomes share this one shape, with no separate enum needed:
///  - success: Success=true, Checks all Passed.
///  - invalid credentials: Success=false, ErrorMessage set.
///  - valid but under-permissioned: Success=true, at least one Failed entry in Checks.
/// </summary>
public sealed record ConnectionTestResult(
    bool Success,
    string? ErrorMessage = null,
    string? Identity = null,
    IReadOnlyList<ConnectionCheck>? Checks = null);
