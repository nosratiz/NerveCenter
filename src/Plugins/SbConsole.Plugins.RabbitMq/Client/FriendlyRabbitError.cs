using System.Security.Authentication;
using RabbitMQ.Client.Exceptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// RabbitMQ counterpart to SbConsole.Sdk.FriendlyError: maps management-API and RabbitMQ.Client
/// failures to short, actionable messages, falling back to the capped/collapsed exception text.
/// Exception types confirmed against the installed RabbitMQ.Client 7.2.2 assembly:
/// AlreadyClosedException derives from OperationInterruptedException (so both are matched by
/// ShutdownReason.ReplyCode first), AuthenticationFailureException derives from
/// PossibleAuthenticationFailureException, and connection-open failures arrive as
/// BrokerUnreachableException (an IOException) wrapping the real cause.
/// </summary>
public static class FriendlyRabbitError
{
    // AMQP 0-9-1 class id of the "connection" class: a 403 there is a login refusal, whereas a 403
    // on a channel method is a per-resource permission refusal.
    private const ushort AmqpConnectionClassId = 10;

    public static string From(Exception ex) => ex switch
    {
        ManagementApiException m => FromManagement(m),
        HttpRequestException { InnerException: AuthenticationException tls } =>
            $"Management API TLS handshake failed — {FriendlyError.Truncate(tls.Message)}",
        HttpRequestException h => $"Management API unreachable — {FriendlyError.Truncate(h.Message)}",
        TaskCanceledException or TimeoutException => "Timed out talking to the broker",
        _ when IsAuthenticationFailure(ex) => "AMQP credentials rejected",
        BrokerUnreachableException => "AMQP endpoint unreachable — check host, port and TLS",
        OperationInterruptedException { ShutdownReason: { ReplyCode: 403, ClassId: AmqpConnectionClassId } } =>
            "AMQP credentials rejected",
        OperationInterruptedException { ShutdownReason.ReplyCode: 405 } => "Resource locked — the queue has an exclusive consumer",
        OperationInterruptedException { ShutdownReason.ReplyCode: 404 } => "Not found on the broker",
        OperationInterruptedException { ShutdownReason.ReplyCode: 403 } => "Access refused by the broker",
        AlreadyClosedException { ShutdownReason.ReplyText: { Length: > 0 } text } =>
            $"Broker closed the connection — {FriendlyError.Truncate(text)}",
        AlreadyClosedException => "Broker closed the connection",
        _ => FriendlyError.From(ex),
    };

    private static string FromManagement(ManagementApiException m) => m.StatusCode switch
    {
        // The broker also answers 401 (not 403) for a vhost the user has no permission on, with
        // reason "User not authorised to access virtual host" -- the credentials themselves are
        // fine there, so say what is actually missing.
        401 when m.Reason?.Contains("virtual host", StringComparison.OrdinalIgnoreCase) == true =>
            $"User has no permission on this vhost ({m.Method} {m.Path} → 401)",
        401 => "Management API rejected the credentials",
        403 => $"User lacks permission or the monitoring/management tag for this vhost ({m.Method} {m.Path} → 403)",
        404 => $"Not found ({m.Method} {m.Path} → 404)",
        >= 500 => $"Management API error {m.StatusCode} ({m.Method} {m.Path})",
        _ => FriendlyError.Truncate(
            $"Management API rejected the request ({m.Method} {m.Path} → {m.StatusCode})"
            + (string.IsNullOrWhiteSpace(m.Reason) ? "" : $": {m.Reason}")),
    };

    // RabbitMQ.Client wraps a refused login inside BrokerUnreachableException, so walk the chain.
    private static bool IsAuthenticationFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PossibleAuthenticationFailureException)
            {
                return true;
            }
        }

        return false;
    }
}
