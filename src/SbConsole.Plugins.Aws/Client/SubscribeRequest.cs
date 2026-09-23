namespace SbConsole.Plugins.Aws.Client;

public sealed record SubscribeRequest(string TopicArn, string Protocol, string Endpoint, bool RawMessageDelivery);
