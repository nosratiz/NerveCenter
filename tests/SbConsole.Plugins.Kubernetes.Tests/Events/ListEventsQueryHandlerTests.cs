using System.Net;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Events;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Events;

public class ListEventsQueryHandlerTests
{
    private const string Secret = "secret";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly Guid _id = Guid.NewGuid();

    public ListEventsQueryHandlerTests()
    {
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns(Secret);
        _ops.ListEventsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.Events());
    }

    private ListEventsQueryHandler Handler() => new(_ops, _connections, NullLogger<ListEventsQueryHandler>.Instance);

    private static EventQuery Query(string? type = null, string? kind = null, string? search = null, IReadOnlyList<string>? namespaces = null) =>
        new(namespaces ?? [], Hour, type, kind, search);

    [Fact]
    public async Task Counts_cover_the_whole_window()
    {
        var result = await Handler().HandleAsync(_id, Query());

        result.IsSuccess.Should().BeTrue();
        var list = result.Value!;
        list.Total.Should().Be(43);
        list.Warnings.Should().Be(38);
        list.Normals.Should().Be(5);
        list.Events.Should().HaveCount(43);
        list.Kinds.Should().Equal("Deployment", "HorizontalPodAutoscaler", "Job", "Node", "Pod");
    }

    [Fact]
    public async Task Type_filter_keeps_counts_over_the_unfiltered_window()
    {
        var result = await Handler().HandleAsync(_id, Query(type: "Warning"));

        var list = result.Value!;
        list.Events.Should().HaveCount(38).And.OnlyContain(e => e.Type == "Warning");
        list.Total.Should().Be(43);
        list.Normals.Should().Be(5);

        (await Handler().HandleAsync(_id, Query(type: "Normal"))).Value!.Events.Should().HaveCount(5).And.OnlyContain(e => e.Type == "Normal");
    }

    [Fact]
    public async Task Kind_filter_keeps_only_that_kind_and_all_kinds_stay_listed()
    {
        var list = (await Handler().HandleAsync(_id, Query(type: "Warning", kind: "Node"))).Value!;

        list.Events.Should().HaveCount(5).And.OnlyContain(e => e.Regarding.Kind == "Node");
        list.Kinds.Should().HaveCount(5);
        list.Warnings.Should().Be(38);
    }

    [Fact]
    public async Task Search_matches_reason_or_message_case_insensitively()
    {
        var byReason = (await Handler().HandleAsync(_id, Query(search: "backoff"))).Value!;
        byReason.Events.Select(e => e.Reason).Should().BeEquivalentTo("BackOff", "BackoffLimitExceeded");

        var byMessage = (await Handler().HandleAsync(_id, Query(search: "METRICS-SERVER"))).Value!;
        byMessage.Events.Should().ContainSingle().Which.Reason.Should().Be("FailedGetResourceMetric");
        byMessage.Total.Should().Be(43);
    }

    [Fact]
    public async Task Blank_search_and_kind_do_not_filter()
    {
        var list = (await Handler().HandleAsync(_id, Query(kind: "", search: "  "))).Value!;

        list.Events.Should().HaveCount(43);
    }

    [Fact]
    public async Task Events_come_back_newest_first()
    {
        _ops.ListEventsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.Events().Reverse().ToList());

        var list = (await Handler().HandleAsync(_id, Query())).Value!;

        list.Events.Select(e => e.LastSeen).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Fetches_the_window_once_for_the_selected_namespaces()
    {
        await Handler().HandleAsync(_id, new EventQuery(["checkout", "monitoring"], TimeSpan.FromHours(6), "Warning", "Pod", "back"));

        await _ops.Received(1).ListEventsAsync(Secret,
            Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(new[] { "checkout", "monitoring" })), TimeSpan.FromHours(6), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_forbidden_list_is_a_friendly_failure()
    {
        _ops.ListEventsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpOperationException("raw")
            {
                Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Forbidden), "{\"kind\":\"Status\",\"message\":\"forbidden\"}"),
            });

        var result = await Handler().HandleAsync(_id, Query());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty().And.NotContain("raw");
    }

    [Fact]
    public async Task Unknown_connection_fails()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Handler().HandleAsync(unknown, Query());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Connection not found.");
    }
}
