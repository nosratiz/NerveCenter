namespace SbConsole.Sdk;

/// <summary>Connection metadata safe to show in UI. Never carries the secret.</summary>
public sealed record ConnectionInfo(
    Guid Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Tags,
    bool? LastTestSucceeded = null,
    DateTimeOffset? LastTestedAt = null,
    string? LastTestError = null)
{
    public bool IsProd => Tags.Contains("prod", StringComparer.OrdinalIgnoreCase);
}
