namespace SbConsole.Sdk;

/// <summary>Outcome of IPlugin.TestConnectionAsync — shown in the Connections page's Status column.</summary>
public sealed record ConnectionTestResult(bool Success, string? ErrorMessage = null);
