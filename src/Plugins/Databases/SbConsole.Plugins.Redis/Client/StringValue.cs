namespace SbConsole.Plugins.Redis.Client;

public sealed record StringValue(string Key, TimeSpan? Ttl, long SizeBytes, string RawValue, bool LooksLikeJson);
