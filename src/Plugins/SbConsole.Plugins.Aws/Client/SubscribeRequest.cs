namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// <paramref name="FilterPolicy"/>/<paramref name="FilterPolicyScope"/> are optional and only sent
/// as Subscribe attributes when a policy is given (see SnsOperations.BuildSubscribeAttributes); a
/// null scope with a policy means SNS's default, MessageAttributes.
/// </summary>
public sealed record SubscribeRequest(
    string TopicArn, string Protocol, string Endpoint, bool RawMessageDelivery,
    string? FilterPolicy = null, string? FilterPolicyScope = null);
