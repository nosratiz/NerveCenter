using FluentAssertions;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Client;

public class AzureServiceBusOperationsTests
{
    // What is testable here without a broker is exactly the client-side half: connection-string
    // parsing (real validation, thrown synchronously before any network call) and the configured
    // retry/timeout options. The catch clauses for server- and transport-side failures are verified
    // by decompiling Azure.Messaging.ServiceBus 7.20.2 / Azure.Core 1.60.0 instead -- see the
    // comments on each clause in AzureServiceBusOperations.TestConnectionAsync. docs/design.md §8
    // keeps real AMQP/HTTP traffic out of this suite.

    [Theory]
    // Every case here must fail at construction time, i.e. purely client-side. A string that
    // parses (even one whose host cannot resolve) would send this test onto the network, which
    // docs/design.md §8 rules out for this suite -- watch the runtime if you add a case.
    [InlineData("this-is-not-a-real-service-bus-connection-string")] // no key=value pairs at all
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/")] // no shared access key at all
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k")] // key name but no key
    [InlineData("Endpoint=;;;")] // present but empty endpoint value
    [InlineData("")] // empty
    public async Task Malformed_connection_strings_get_one_fixed_readable_message(string connectionString)
    {
        var result = await new AzureServiceBusOperations().TestConnectionAsync(connectionString);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid connection string format");
    }

    [Fact]
    public void A_well_formed_connection_string_gets_past_the_parse_check()
    {
        // Guards the construction-time catch against over-reaching: a syntactically valid string
        // must not be reported as malformed. Constructing the client is pure client-side parsing --
        // no request is issued until an operation is called -- so this stays offline. Calling
        // TestConnectionAsync with it would attempt a real DNS lookup, which docs/design.md §8
        // keeps out of this suite.
        var act = () => new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(
            "Endpoint=sb://sbconsole-example.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=cGxhY2Vob2xkZXI=",
            AzureServiceBusOperations.CreateAdministrationClientOptions());

        act.Should().NotThrow();
    }

    [Fact]
    public async Task TestConnectionAsync_never_throws_regardless_of_input()
    {
        var act = async () => await new AzureServiceBusOperations().TestConnectionAsync("");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task No_failure_path_ever_returns_an_unbounded_error_message()
    {
        var result = await new AzureServiceBusOperations().TestConnectionAsync("Endpoint=;;;");

        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Length.Should().BeLessThanOrEqualTo(FriendlyError.MaxLength + FriendlyError.TruncationMarker.Length);
    }

    // docs/design.md §7 wants "a clear 'namespace unreachable' state instead of indefinite
    // spinners". The behavioral proof of that needs an unreachable broker, which is out of scope
    // for a unit suite, so these assert the configuration that produces it. Both client types are
    // covered because they take different options types: the admin client is HTTP (Azure.Core
    // RetryOptions.NetworkTimeout), the messaging client is AMQP (ServiceBusRetryOptions.TryTimeout).

    [Fact]
    public void Administration_client_options_bound_a_single_attempt_and_the_retry_count()
    {
        var options = AzureServiceBusOperations.CreateAdministrationClientOptions();

        options.Retry.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(10));
        options.Retry.MaxRetries.Should().Be(2);
        options.Retry.MaxDelay.Should().Be(TimeSpan.FromSeconds(5));
        // Not the SDK defaults (100s / 3 retries), which is the whole point.
        options.Retry.NetworkTimeout.Should().BeLessThan(TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void Messaging_client_options_bound_a_single_attempt_and_the_retry_count()
    {
        var options = AzureServiceBusOperations.CreateClientOptions();

        options.RetryOptions.TryTimeout.Should().Be(TimeSpan.FromSeconds(10));
        options.RetryOptions.MaxRetries.Should().Be(2);
        options.RetryOptions.MaxDelay.Should().Be(TimeSpan.FromSeconds(5));
        // Not the SDK default of 60s.
        options.RetryOptions.TryTimeout.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void The_looping_operations_have_a_wall_clock_cap()
    {
        // Purge's drain loop and resubmit's scan loop are the two operations whose iteration count
        // the retry options above cannot bound; each links a CancellationTokenSource with
        // CancelAfter(BulkOperationTimeout). Live testing saw a purge run past 12 minutes.
        AzureServiceBusOperations.BulkOperationTimeout.Should().Be(TimeSpan.FromMinutes(2));
    }
}
