namespace SbConsole.Plugins.Redis.Client;

/// <summary>OBJECT ENCODING / OBJECT IDLETIME for the Keys page's value-panel header ("encoding hashtable · idle 3s"). Either can be null: the key may have vanished between calls, or the server's maxmemory-policy may be LFU, under which OBJECT IDLETIME is refused.</summary>
public sealed record KeyMetadata(string? Encoding, TimeSpan? IdleTime);
