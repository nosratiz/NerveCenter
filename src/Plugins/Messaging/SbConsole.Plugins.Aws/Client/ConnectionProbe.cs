using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// Runs one Test-connection permission probe (e.g. ListQueues, ListTopics) and reduces its outcome
/// to a ConnectionCheck. A denied/failed probe is a Failed check, never a failed connection test --
/// the credentials already proved valid via GetCallerIdentity by the time any probe runs. Extracted
/// from SqsOperations.TestConnectionAsync so the pass/fail mapping is unit-testable without AWS.
/// </summary>
internal static class ConnectionProbe
{
    public static async Task<ConnectionCheck> RunAsync(string label, Func<CancellationToken, Task<string>> probe, CancellationToken ct)
    {
        try
        {
            return new ConnectionCheck(label, ConnectionCheckStatus.Passed, await probe(ct));
        }
        catch (Exception ex) when (ct.IsCancellationRequested is false)
        {
            return new ConnectionCheck(label, ConnectionCheckStatus.Failed, FriendlyAwsError.From(ex));
        }
    }
}
