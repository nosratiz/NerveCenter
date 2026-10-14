using System.Text.Json;

namespace SbConsole.Plugins.Aws.Subscriptions;

/// <summary>
/// Client-side check run before any SetSubscriptionAttributes/Subscribe call and live in the
/// filter-policy editors. Deliberately shallow: it only guarantees the shape SNS needs at the top
/// level (a JSON object) and a known scope -- SNS's own operator/nesting rules are left to SNS,
/// whose rejection surfaces through FriendlyAwsError.
/// </summary>
internal static class FilterPolicyValidator
{
    public const string MessageAttributes = "MessageAttributes";
    public const string MessageBody = "MessageBody";

    /// <param name="IsClear">Empty/whitespace input -- valid, and means "remove the filter policy".</param>
    internal sealed record Result(bool IsValid, bool IsClear, string? Error);

    public static Result Validate(string? policyJson, string scope)
    {
        if (scope is not (MessageAttributes or MessageBody))
        {
            return new Result(false, false, "Filter policy scope must be MessageAttributes or MessageBody.");
        }

        if (string.IsNullOrWhiteSpace(policyJson))
        {
            return new Result(true, true, null);
        }

        try
        {
            using var document = JsonDocument.Parse(policyJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? new Result(true, false, null)
                : new Result(false, false, """Filter policy must be a JSON object, e.g. {"region":["uk"]}.""");
        }
        catch (JsonException)
        {
            return new Result(false, false, "Filter policy isn't valid JSON.");
        }
    }
}
