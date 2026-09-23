namespace SbConsole.Sdk;

/// <summary>One named diagnostic probe a plugin ran as part of TestConnectionAsync (e.g. "Queues visible").</summary>
public sealed record ConnectionCheck(string Label, ConnectionCheckStatus Status, string? Detail = null);

public enum ConnectionCheckStatus { Passed, Failed }
