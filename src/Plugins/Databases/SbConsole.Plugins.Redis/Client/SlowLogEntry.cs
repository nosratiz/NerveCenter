namespace SbConsole.Plugins.Redis.Client;

public sealed record SlowLogEntry(DateTimeOffset At, TimeSpan Duration, string Command, string? ClientAddress);
