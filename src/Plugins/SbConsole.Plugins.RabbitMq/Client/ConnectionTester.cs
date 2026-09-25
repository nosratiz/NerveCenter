using System.Globalization;
using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// Test connection's split result (design spec §2): the AMQP probe and the management-API probe
/// run concurrently, each under its own 10 s timeout, and the outcome matrix maps onto the plain
/// <see cref="ConnectionTestResult"/> shape -- both pass: success; one passes: success with one
/// Failed check whose Detail spells out the consequence; both fail: failure carrying the AMQP
/// error (credential problems surface there first). The probes are injected so the matrix is
/// unit-tested without a broker; <see cref="RabbitOperations"/> wires in the real clients.
/// </summary>
internal sealed class ConnectionTester(
    Func<RabbitConnectionSettings, CancellationToken, Task> amqpProbe,
    Func<RabbitConnectionSettings, CancellationToken, Task<ManagementProbe>> managementProbe,
    TimeProvider clock,
    ILogger? logger = null)
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private const string ManagementDownConsequence =
        " — messages will send and receive, but queue depths, rates and bindings stay blank until the management plugin is reachable.";

    private const string AmqpDownConsequence =
        " — the console can read the broker but cannot publish or get messages.";

    public async Task<ConnectionTestResult> TestAsync(string secret, CancellationToken ct = default)
    {
        RabbitConnectionSettings settings;
        try
        {
            settings = RabbitConnectionSettings.From(secret);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, FriendlyRabbitError.From(ex));
        }

        var amqpTask = RunAsync("AMQP", async token =>
        {
            await amqpProbe(settings, token);
            return true;
        }, ct);
        var managementTask = RunAsync("management API", token => managementProbe(settings, token), ct);
        await Task.WhenAll(amqpTask, managementTask);

        var amqp = await amqpTask;
        var management = await managementTask;

        var amqpLabel = $"AMQP {settings.AmqpPort.ToString(CultureInfo.InvariantCulture)}" + TransportSuffix(settings);
        var managementLabel = $"Management API {settings.ManagementBaseUri.Port.ToString(CultureInfo.InvariantCulture)}";

        if (amqp.Error is null && management.Error is null)
        {
            return new ConnectionTestResult(
                true,
                Identity: FullIdentity(settings, management.Value!),
                Checks:
                [
                    new ConnectionCheck(amqpLabel, ConnectionCheckStatus.Passed, Latency(amqp.Elapsed)),
                    new ConnectionCheck(managementLabel, ConnectionCheckStatus.Passed, Latency(management.Elapsed)),
                ]);
        }

        if (amqp.Error is null)
        {
            return new ConnectionTestResult(
                true,
                Identity: $"vhost {settings.Vhost}",
                Checks:
                [
                    new ConnectionCheck(amqpLabel, ConnectionCheckStatus.Passed, Latency(amqp.Elapsed)),
                    new ConnectionCheck(managementLabel, ConnectionCheckStatus.Failed, management.Error + ManagementDownConsequence),
                ]);
        }

        if (management.Error is null)
        {
            return new ConnectionTestResult(
                true,
                Identity: FullIdentity(settings, management.Value!),
                Checks:
                [
                    new ConnectionCheck(amqpLabel, ConnectionCheckStatus.Failed, amqp.Error + AmqpDownConsequence),
                    new ConnectionCheck(managementLabel, ConnectionCheckStatus.Passed, Latency(management.Elapsed)),
                ]);
        }

        return new ConnectionTestResult(
            false,
            ErrorMessage: amqp.Error,
            Checks:
            [
                new ConnectionCheck(amqpLabel, ConnectionCheckStatus.Failed, amqp.Error),
                new ConnectionCheck(managementLabel, ConnectionCheckStatus.Failed, management.Error),
            ]);
    }

    private async Task<ProbeResult<T>> RunAsync<T>(string name, Func<CancellationToken, Task<T>> probe, CancellationToken ct)
    {
        // The timeout runs on the injected clock so tests can drive it; WaitAsync also abandons a
        // probe that ignores its token (a hung TCP connect must not hold Test connection hostage).
        using var timeout = new CancellationTokenSource(ProbeTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var started = clock.GetTimestamp();
        try
        {
            var value = await probe(linked.Token).WaitAsync(linked.Token);
            return new ProbeResult<T>(value, null, clock.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = timeout.IsCancellationRequested
                ? FriendlyRabbitError.From(new TimeoutException())
                : FriendlyRabbitError.From(ex);
            logger?.LogWarning(ex, "RabbitMQ Test connection: {Probe} probe failed", name);
            return new ProbeResult<T>(default, error, clock.GetElapsedTime(started));
        }
    }

    private static string TransportSuffix(RabbitConnectionSettings settings) =>
        !settings.Tls ? " · plain"
        : settings.VerifyCert ? " · TLS verified"
        : " · TLS (unverified)";

    private static string FullIdentity(RabbitConnectionSettings settings, ManagementProbe probe) =>
        string.Create(CultureInfo.InvariantCulture,
            $"RabbitMQ {probe.RabbitVersion} · vhost {settings.Vhost} · {probe.ExchangeCount} exchanges · {probe.QueueCount} queues · tags {(probe.UserTags.Count == 0 ? "none" : string.Join(", ", probe.UserTags))}");

    private static string Latency(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(elapsed.TotalMilliseconds):0} ms");

    private sealed record ProbeResult<T>(T? Value, string? Error, TimeSpan Elapsed);
}
