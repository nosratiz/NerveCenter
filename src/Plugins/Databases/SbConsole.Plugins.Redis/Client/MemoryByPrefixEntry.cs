namespace SbConsole.Plugins.Redis.Client;

public sealed record MemoryByPrefixEntry(string Prefix, long SampledBytes, int SampledKeyCount, int KeysWithoutTtlCount);

internal readonly record struct SampledKey(string Key, long Bytes, bool HasTtl);
