using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record SendMessageCommand(
    Guid ConnectionId, string ConnectionName, string QueueName, string Body, string ContentType,
    IReadOnlyDictionary<string, string>? Properties, DateTimeOffset? ScheduledEnqueueTime);

public sealed class SendMessageCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<SendMessageCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(SendMessageCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var request = new SendMessageRequest(cmd.Body, cmd.ContentType, cmd.Properties, cmd.ScheduledEnqueueTime);
        try
        {
            // Inside the try: Unprotect can throw on a wrong-key ciphertext (e.g. after an
            // SBC_DATA_KEY rotation), and that must not escape this handler.
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.SendMessageAsync(secret, cmd.QueueName, request, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sending a message to {Target} failed.", target);
            await audit.RecordAsync("message.send", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("message.send", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
