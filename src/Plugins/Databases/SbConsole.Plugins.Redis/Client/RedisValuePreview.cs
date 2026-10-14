namespace SbConsole.Plugins.Redis.Client;

public static class RedisValuePreview
{
    /// <summary>A cheap, non-parsing heuristic for StringValue.LooksLikeJson: the trimmed value starts with '{' or '['.</summary>
    public static bool LooksLikeJson(string value)
    {
        var trimmed = value.AsSpan().Trim();
        return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
    }
}
