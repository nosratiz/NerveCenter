using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Architecture;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Sdk;
using static SbConsole.Plugins.Kubernetes.Tests.Architecture.ArchTestData;

namespace SbConsole.Plugins.Kubernetes.Tests.Architecture;

public class ArchitectureHandlerTests
{
    private static readonly string Secret = TestData.Kubeconfig();
    private static readonly string Gateway = W("checkout", "payment-gateway");
    private static readonly string Cart = W("checkout", "cart");
    private static readonly string CartRedis = W("checkout", "cart-redis", K8sWorkloadKind.StatefulSet);

    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly Guid _id = Guid.NewGuid();
    private readonly ArchEditContext _ctx;

    public ArchitectureHandlerTests()
    {
        _ctx = new ArchEditContext(_id, "aks-prod-eu", "a.hassan");
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns(Secret);
        _ops.GetArchitectureInputsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(_ => Cluster());
    }

    private GetArchitectureQueryHandler Query() => new(_ops, _connections, _store, NullLogger<GetArchitectureQueryHandler>.Instance);
    private SaveDescriptionCommandHandler Save() => new(_connections, _audit, _store, _clock, NullLogger<SaveDescriptionCommandHandler>.Instance);
    private AddDependencyCommandHandler Add() => new(_connections, _audit, _store, _clock, NullLogger<AddDependencyCommandHandler>.Instance);
    private RemoveDependencyCommandHandler Remove() => new(_connections, _audit, _store, NullLogger<RemoveDependencyCommandHandler>.Instance);
    private RestoreDismissedCommandHandler Restore() => new(_connections, _audit, _store, NullLogger<RestoreDismissedCommandHandler>.Instance);

    [Fact]
    public async Task Query_builds_the_graph_for_the_selection_and_never_audits()
    {
        var r = await Query().HandleAsync(_id, Scope);

        r.IsSuccess.Should().BeTrue();
        r.Value!.Graph.Nodes.Should().Contain(n => n.Id == Gateway);
        r.Value.Descriptions.Should().BeEmpty();
        await _ops.Received(1).GetArchitectureInputsAsync(Secret, Scope, Arg.Any<CancellationToken>());
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Query_failure_is_a_friendly_error()
    {
        _ops.GetArchitectureInputsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("boom"));

        var r = await Query().HandleAsync(_id, Scope);

        r.IsSuccess.Should().BeFalse();
        r.Error.Should().Be("Can't reach the cluster.");
    }

    [Fact]
    public async Task Saving_a_description_versions_it_and_audits_without_the_text()
    {
        var r = await Save().HandleAsync(new SaveDescriptionCommand(_ctx, Gateway, "  Fronts **Stripe**.  ", 0));

        r.Value.Should().Be(new ArchDescription("Fronts **Stripe**.", 1, "a.hassan", _clock.GetUtcNow()));
        await _audit.Received(1).RecordAsync("k8s.arch.edit", "aks-prod-eu/checkout/payment-gateway", ActionRisk.Mutating, true,
            "description v1", Arg.Any<CancellationToken>());

        var view = await Query().HandleAsync(_id, Scope);
        view.Value!.Descriptions[Gateway].Text.Should().Be("Fronts **Stripe**.");

        var second = await Save().HandleAsync(new SaveDescriptionCommand(_ctx with { Author = "b.lee" }, Gateway, "v2 text", 1));
        second.Value!.Version.Should().Be(2);
        second.Value.Author.Should().Be("b.lee");
    }

    [Fact]
    public async Task A_stale_editor_is_refused_and_not_audited()
    {
        await Save().HandleAsync(new SaveDescriptionCommand(_ctx, Gateway, "first", 0));
        _audit.ClearReceivedCalls();

        var r = await Save().HandleAsync(new SaveDescriptionCommand(_ctx with { Author = "b.lee" }, Gateway, "mine", 0));

        r.IsSuccess.Should().BeFalse();
        r.Error.Should().Be("a.hassan saved v1 since you opened it — reload to see it.");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Clearing_a_description_removes_it()
    {
        await Save().HandleAsync(new SaveDescriptionCommand(_ctx, Gateway, "first", 0));

        var r = await Save().HandleAsync(new SaveDescriptionCommand(_ctx, Gateway, "   ", 1));

        r.IsSuccess.Should().BeTrue();
        r.Value.Should().BeNull();
        (await Query().HandleAsync(_id, Scope)).Value!.Descriptions.Should().BeEmpty();
        await _audit.Received(1).RecordAsync("k8s.arch.edit", Arg.Any<string>(), ActionRisk.Mutating, true, "description cleared", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unchanged_or_too_long_descriptions_are_refused()
    {
        (await Save().HandleAsync(new SaveDescriptionCommand(_ctx, Gateway, "", 0))).Error.Should().Be("Nothing changed.");
        (await Save().HandleAsync(new SaveDescriptionCommand(_ctx, Gateway, new string('a', 4001), 0))).Error
            .Should().Be("A description is at most 4000 characters.");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_connection_fails_without_audit()
    {
        var other = Guid.NewGuid();
        _connections.GetSecretAsync(other, Arg.Any<CancellationToken>()).Returns((string?)null);

        var r = await Save().HandleAsync(new SaveDescriptionCommand(_ctx with { ConnectionId = other }, Gateway, "x", 0));

        r.Error.Should().Be("Connection not found.");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task A_store_failure_is_audited_as_failed()
    {
        var store = Substitute.For<IPluginStore>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        store.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db locked"));
        var handler = new SaveDescriptionCommandHandler(_connections, _audit, store, _clock, NullLogger<SaveDescriptionCommandHandler>.Instance);

        var r = await handler.HandleAsync(new SaveDescriptionCommand(_ctx, Gateway, "x", 0));

        r.Error.Should().Be("Saving to the console failed — try again.");
        await _audit.Received(1).RecordAsync("k8s.arch.edit", Arg.Any<string>(), ActionRisk.Mutating, false,
            "Saving to the console failed — try again.", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adding_an_external_dependency_shows_it_on_the_map()
    {
        var r = await Add().HandleAsync(new AddDependencyCommand(_ctx, Gateway, "https://api.stripe.com/v1", ArchEdgeKind.Http, "HTTPS"));

        r.Value!.To.Should().Be("x:api.stripe.com");
        await _audit.Received(1).RecordAsync("k8s.arch.edit", "aks-prod-eu/checkout/payment-gateway → api.stripe.com", ActionRisk.Mutating, true,
            "added manual http dependency (HTTPS)", Arg.Any<CancellationToken>());

        var graph = (await Query().HandleAsync(_id, Scope)).Value!.Graph;
        graph.Edges.Should().ContainSingle(e => e.To == "x:api.stripe.com" && e.Manual);
    }

    [Fact]
    public async Task Duplicate_or_self_dependencies_are_refused()
    {
        await Add().HandleAsync(new AddDependencyCommand(_ctx, Gateway, "api.stripe.com", ArchEdgeKind.Http, null));
        _audit.ClearReceivedCalls();

        (await Add().HandleAsync(new AddDependencyCommand(_ctx, Gateway, "x:api.stripe.com", ArchEdgeKind.Http, null)))
            .Error.Should().Be("That dependency is already on the map.");
        (await Add().HandleAsync(new AddDependencyCommand(_ctx, Gateway, Gateway, ArchEdgeKind.Http, null)))
            .Error.Should().Be("A service can't depend on itself.");
        (await Add().HandleAsync(new AddDependencyCommand(_ctx, Gateway, "not a host!", ArchEdgeKind.Http, null)))
            .Error.Should().Be("Pick a node, or type a host such as api.stripe.com.");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_detected_edge_dismisses_it_until_restored()
    {
        var graph = (await Query().HandleAsync(_id, Scope)).Value!.Graph;
        var edge = graph.Edges.Single(e => e.From == Cart && e.To == CartRedis);

        (await Remove().HandleAsync(new RemoveDependencyCommand(_ctx, edge))).IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("k8s.arch.edit", "aks-prod-eu/checkout/cart → checkout/cart-redis", ActionRisk.Mutating, true,
            "dismissed detected dependency (env REDIS_HOST)", Arg.Any<CancellationToken>());

        var after = (await Query().HandleAsync(_id, Scope)).Value!.Graph;
        after.Edges.Should().NotContain(e => e.Id == edge.Id);
        after.Dismissed.Should().Be(1);

        (await Restore().HandleAsync(_ctx)).IsSuccess.Should().BeTrue();
        await _audit.Received(1).RecordAsync("k8s.arch.edit", "aks-prod-eu/dismissed dependencies", ActionRisk.Mutating, true,
            "restored 1 dismissed dependency", Arg.Any<CancellationToken>());
        (await Query().HandleAsync(_id, Scope)).Value!.Graph.Edges.Should().Contain(e => e.Id == edge.Id);
        (await Restore().HandleAsync(_ctx)).Error.Should().Be("Nothing changed.");
    }

    [Fact]
    public async Task Removing_a_manual_edge_deletes_it()
    {
        var added = (await Add().HandleAsync(new AddDependencyCommand(_ctx, Gateway, "api.stripe.com", ArchEdgeKind.Http, null))).Value!;

        (await Remove().HandleAsync(new RemoveDependencyCommand(_ctx, added))).IsSuccess.Should().BeTrue();

        var graph = (await Query().HandleAsync(_id, Scope)).Value!.Graph;
        graph.Edges.Should().NotContain(e => e.Manual);
        graph.Nodes.Should().NotContain(n => n.Id == "x:api.stripe.com");
        (await Remove().HandleAsync(new RemoveDependencyCommand(_ctx, added))).Error
            .Should().Be("That dependency was already removed — reload to see the map.");
    }

    [Theory]
    [InlineData("api.stripe.com", "x:api.stripe.com")]
    [InlineData("API.Stripe.com:443", "x:api.stripe.com")]
    [InlineData("https://hooks.example.com/in?x=1", "x:hooks.example.com")]
    [InlineData("w:checkout/Deployment/cart", "w:checkout/Deployment/cart")]
    [InlineData("", null)]
    [InlineData("localhost", null)]
    public void Targets_normalise_hosts_and_pass_node_ids(string input, string? expected)
    {
        AddDependencyCommandHandler.Target(input).Should().Be(expected);
    }
}
