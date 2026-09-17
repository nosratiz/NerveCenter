using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class SubscriptionPeekPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();

    public SubscriptionPeekPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        SeedConnection(isProd: false);
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<PeekSubscriptionMessagesQueryHandler>();
        Services.AddSingleton(_confirmation);
        Services.AddSingleton(new ResubmitSubscriptionDeadLetterMessagesCommandHandler(_operations, _connections, Substitute.For<IAuditScope>(), NullLogger<ResubmitSubscriptionDeadLetterMessagesCommandHandler>.Instance));
        Services.AddSingleton(new PurgeSubscriptionDeadLetterMessagesCommandHandler(_operations, _connections, Substitute.For<IAuditScope>(), NullLogger<PurgeSubscriptionDeadLetterMessagesCommandHandler>.Instance));
    }

    private void SeedConnection(bool isProd, string name = "sb-conn")
    {
        var tags = isProd ? new[] { "prod" } : Array.Empty<string>();
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, name, "azure-servicebus", tags) });
    }

    private void NavigateToPeekQuery(Guid connectionId, bool deadLetter, long? deadLetterCount = null)
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var queryParams = new Dictionary<string, object?>
        {
            ["ConnectionId"] = connectionId,
            ["DeadLetter"] = deadLetter,
        };
        if (deadLetterCount is not null)
        {
            queryParams["DeadLetterCount"] = deadLetterCount;
        }

        navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameters(queryParams));
    }

    private IRenderedComponent<SbConsole.Plugins.ServiceBus.Pages.SubscriptionPeek> RenderPage() =>
        Render<SbConsole.Plugins.ServiceBus.Pages.SubscriptionPeek>(parameters => parameters
            .Add(p => p.TopicName, "orders")
            .Add(p => p.SubscriptionName, "uk-team"));

    [Fact]
    public async Task Shows_message_list_and_selecting_one_shows_its_body()
    {
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage>
            {
                new(1, "BODY-ONE", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()),
                new(2, "BODY-TWO", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()),
            });

        NavigateToPeekQuery(_connectionId, false);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("BODY-ONE");
        cut.Markup.Should().NotContain("BODY-TWO");
    }

    [Fact]
    public async Task Dead_letter_mode_shows_resubmit_and_purge_actions_but_regular_mode_does_not()
    {
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()) });
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

        NavigateToPeekQuery(_connectionId, false);
        var regularModeCut = RenderPage();
        await Task.Delay(30);
        regularModeCut.Render();

        regularModeCut.FindAll("button.purge-queue").Should().BeEmpty();

        NavigateToPeekQuery(_connectionId, true);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll("button.purge-queue").Should().HaveCount(1);
    }

    [Fact]
    public async Task Resubmit_selected_calls_the_operations_seam_with_the_checked_sequence_numbers()
    {
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });
        _operations.ResubmitSubscriptionDeadLetterMessagesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        NavigateToPeekQuery(_connectionId, true);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();
        cut.Find("input.select-message").Change(true);
        cut.Find("button.resubmit-selected").Click();
        await Task.Delay(30);

        await _operations.Received(1).ResubmitSubscriptionDeadLetterMessagesAsync(
            "Endpoint=sb://real", "orders", "uk-team",
            Arg.Is<IReadOnlyList<long>>(l => l.SequenceEqual(new long[] { 1 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_passes_the_connections_prod_flag_to_the_confirmation_prompt_not_a_url_parameter()
    {
        // Direct analogue of PeekPageTests.cs's identically-named test: applies the same
        // security pattern from day one instead of retrofitting it. NavigateToPeekQuery below
        // carries no isProd (or any prod-related) query parameter at all.
        SeedConnection(isProd: true);
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

        NavigateToPeekQuery(_connectionId, true);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.purge-queue").Click();
        await Task.Delay(30);

        await _confirmation.Received(1).ConfirmAsync("Purge", "uk-team", true, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_is_disabled_until_the_real_connection_record_has_resolved()
    {
        var pendingLookup = new TaskCompletionSource<IReadOnlyList<ConnectionInfo>>();
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>()).Returns(pendingLookup.Task);
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

        NavigateToPeekQuery(_connectionId, true);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.purge-queue").HasAttribute("disabled").Should().BeTrue();

        pendingLookup.SetResult(new List<ConnectionInfo> { new(_connectionId, "sb-conn", "azure-servicebus", ["prod"]) });
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.purge-queue").HasAttribute("disabled").Should().BeFalse();
    }
}
