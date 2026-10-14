namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// A filter-policy update that needed two SetSubscriptionAttributes calls (policy + scope) failed
/// after the first one succeeded, so the subscription is left in a mixed state. The message says
/// which half landed; InnerException is the failure of the second call. Mirrors Move to source's
/// "copied but still in the DLQ" partial-failure error: never looks like a plain, retry-safe failure.
/// </summary>
public sealed class FilterPolicyPartiallyAppliedException(string message, Exception innerException)
    : Exception(message, innerException);
