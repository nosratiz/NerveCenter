using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class RabbitOperationsTests
{
    private const string Malformed = "username=u;password=p";

    public static TheoryData<string, Func<RabbitOperations, Task>> Operations() => new()
    {
        { "ListVhosts", ops => ops.ListVhostsAsync(Malformed) },
        { "GetOverview", ops => ops.GetOverviewAsync(Malformed) },
        { "ListQueues", ops => ops.ListQueuesAsync(Malformed, "/") },
        { "PurgeQueue", ops => ops.PurgeQueueAsync(Malformed, "/", "q") },
        { "GetMessages", ops => ops.GetMessagesAsync(Malformed, "/", "q", 1, GetMode.Peek) },
        { "Publish", ops => ops.PublishAsync(Malformed, "/", new PublishRequest("", "q", [])) },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task A_malformed_secret_surfaces_as_the_settings_exception(string name, Func<RabbitOperations, Task> call)
    {
        _ = name;
        var ops = new RabbitOperations();

        await call.Invoking(c => c(ops)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Connection is missing 'host'.");
    }
}
