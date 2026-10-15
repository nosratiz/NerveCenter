using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Architecture;
using SbConsole.Plugins.Kubernetes.Client;
using static SbConsole.Plugins.Kubernetes.Tests.Architecture.ArchTestData;

namespace SbConsole.Plugins.Kubernetes.Tests.Architecture;

public class ArchitectureBuilderTests
{
    private static readonly string Gateway = W("checkout", "payment-gateway");
    private static readonly string CheckoutApi = W("checkout", "checkout-api");
    private static readonly string Cart = W("checkout", "cart");
    private static readonly string Pricing = W("checkout", "pricing");
    private static readonly string CartRedis = W("checkout", "cart-redis", K8sWorkloadKind.StatefulSet);
    private static readonly string Kafka = W("platform", "kafka", K8sWorkloadKind.StatefulSet);
    private static readonly string PgMain = W("data", "pg-main", K8sWorkloadKind.StatefulSet);
    private static readonly string Ledger = W("payments", "ledger");
    private static readonly string Settlement = W("payments", "settlement-api");

    private static ArchitectureGraph Build(ArchOverlay? overlay = null, IReadOnlyList<string>? scope = null) =>
        ArchitectureBuilder.Build(Cluster(), scope ?? Scope, overlay ?? ArchOverlay.Empty);

    private static ArchEdge Edge(ArchitectureGraph g, string from, string to) =>
        g.Edges.Single(e => e.From == from && e.To == to);

    private static ArchNode Node(ArchitectureGraph g, string id) => g.Nodes.Single(n => n.Id == id);

    [Fact]
    public void Selected_namespaces_plus_their_dependencies_become_nodes()
    {
        var g = Build();

        g.Nodes.Where(n => n.InScope).Select(n => n.Name).Should().BeEquivalentTo(
            "shop", "checkout-api", "payment-gateway", "cart", "pricing", "cart-redis", "settlement-api", "ledger", "webhooks");
        g.Nodes.Where(n => !n.InScope).Select(n => n.Id).Should().BeEquivalentTo(Kafka, PgMain, "x:hooks.example.com");
        g.Nodes.Should().NotContain(n => n.Name == "unrelated", "a workload outside the selection is shown only as a dependency");
    }

    [Fact]
    public void All_namespaces_when_the_scope_is_empty()
    {
        var g = Build(scope: []);

        g.Nodes.Should().Contain(n => n.Name == "unrelated" && n.InScope);
        Node(g, Kafka).InScope.Should().BeTrue();
    }

    [Fact]
    public void Service_dns_in_env_resolves_short_namespaced_and_fully_qualified_names()
    {
        var g = Build();

        Edge(g, CheckoutApi, Gateway).Should().Be(new ArchEdge(
            ArchEdge.MakeId(CheckoutApi, Gateway), CheckoutApi, Gateway, ArchEdgeKind.Http, "HTTP · 8080", "env PAYMENTS_URL", false));
        Edge(g, CheckoutApi, Cart).Source.Should().Be("env CART_URL");
        Edge(g, Settlement, Ledger).Kind.Should().Be(ArchEdgeKind.Http);
        Edge(g, Gateway, Kafka).Source.Should().Be("env KAFKA_BROKERS");
        Edge(g, Ledger, Kafka).Source.Should().Be("env EVENTS");
        Edge(g, Ledger, PgMain).Source.Should().Be("env DATABASE_URL");
    }

    [Fact]
    public void Store_and_queue_edges_are_classified_by_scheme_or_port()
    {
        var g = Build();

        Edge(g, Gateway, CartRedis).Should().Match<ArchEdge>(e => e.Kind == ArchEdgeKind.Store && e.Label == "redis · 6379");
        Edge(g, Cart, CartRedis).Should().Match<ArchEdge>(e => e.Kind == ArchEdgeKind.Store && e.Label == "redis · 6379");
        Edge(g, Gateway, Kafka).Should().Match<ArchEdge>(e => e.Kind == ArchEdgeKind.Queue && e.Label == "kafka · 9092");
        Edge(g, Ledger, PgMain).Should().Match<ArchEdge>(e => e.Kind == ArchEdgeKind.Store && e.Label == "postgres · 5432");
    }

    [Fact]
    public void Ado_style_connection_strings_are_detected()
    {
        var g = Build();

        Edge(g, Pricing, PgMain).Should().Match<ArchEdge>(e =>
            e.Kind == ArchEdgeKind.Store && e.Label == "postgres · 5432" && e.Source == "env DB");
    }

    [Fact]
    public void Edges_never_carry_env_values()
    {
        var g = Build();

        g.Edges.Should().NotContain(e => e.Label.Contains("hunter2") || e.Source.Contains("hunter2")
            || e.Label.Contains("s3cret") || e.Source.Contains("s3cret") || e.Label.Contains("pw"));
    }

    [Fact]
    public void Ingress_routes_to_the_workloads_behind_its_backend_services()
    {
        var g = Build();

        var shop = Node(g, "i:checkout/shop");
        shop.Kind.Should().Be(ArchNodeKind.Ingress);
        shop.Subtitle.Should().Be("Ingress · 2 hosts");
        Edge(g, "i:checkout/shop", CheckoutApi).Should().Match<ArchEdge>(e => e.Kind == ArchEdgeKind.Http && e.Source == "Ingress shop");
    }

    [Fact]
    public void Http_urls_to_hosts_outside_the_cluster_are_external_nodes()
    {
        var g = Build();

        var hooks = Node(g, "x:hooks.example.com");
        hooks.Kind.Should().Be(ArchNodeKind.External);
        hooks.Subtitle.Should().Be("external · HTTPS");
        Edge(g, W("payments", "webhooks"), "x:hooks.example.com").Label.Should().Be("HTTPS");
    }

    [Fact]
    public void An_unresolvable_short_name_is_ignored_not_external()
    {
        var g = Build(scope: []);

        g.Nodes.Should().NotContain(n => n.Name == "nowhere");
        g.Edges.Should().NotContain(e => e.From == W("platform", "unrelated"));
    }

    [Fact]
    public void Stores_are_recognised_by_image()
    {
        var g = Build();

        Node(g, CartRedis).Should().Match<ArchNode>(n => n.Kind == ArchNodeKind.Store && n.StoreType == "Redis"
            && n.Subtitle == "Redis · StatefulSet · checkout");
        Node(g, Kafka).StoreType.Should().Be("Kafka");
        Node(g, PgMain).StoreType.Should().Be("PostgreSQL");
        Node(g, Gateway).Kind.Should().Be(ArchNodeKind.Workload);
    }

    [Fact]
    public void Health_subtitle_team_and_version_come_from_the_workload()
    {
        var g = Build();

        Node(g, Gateway).Should().Match<ArchNode>(n => n.Health == ArchHealth.Failing && n.Subtitle == "2/4 · unavailable"
            && n.Team == "payments" && n.Version == "2.14.1");
        Node(g, CheckoutApi).Should().Match<ArchNode>(n => n.Health == ArchHealth.Ok && n.Subtitle == "Deployment · 6/6");
        Node(g, W("payments", "webhooks")).Should().Match<ArchNode>(n => n.Health == ArchHealth.Idle && n.Subtitle == "Deployment · scaled to 0");
    }

    [Fact]
    public void A_service_no_workload_backs_becomes_a_service_node()
    {
        var inputs = new K8sArchitectureInputs(
            [Workload("a", "api", env: [("Q", "amqp://broker:5672"), ("LEGACY", "http://legacy-db:80")])],
            [Service("a", "broker", app: "nothing-matches", port: 5672), Service("a", "legacy-db", externalName: "db.corp.example")],
            []);

        var g = ArchitectureBuilder.Build(inputs, ["a"], ArchOverlay.Empty);

        Node(g, "s:a/broker").Should().Match<ArchNode>(n => n.Kind == ArchNodeKind.Service && n.Subtitle == "Service · no pods");
        Edge(g, W("a", "api"), "s:a/broker").Should().Match<ArchEdge>(e => e.Kind == ArchEdgeKind.Queue && e.Label == "amqp · 5672");
        Node(g, "x:db.corp.example").Should().Match<ArchNode>(n => n.Kind == ArchNodeKind.External && n.Subtitle == "ExternalName · legacy-db");
    }

    [Fact]
    public void Self_references_and_duplicates_collapse()
    {
        var inputs = new K8sArchitectureInputs(
            [Workload("a", "api", env: [("SELF", "http://api:8080"), ("B1", "http://b:80"), ("B2", "http://b.a:80")]), Workload("a", "b")],
            [Service("a", "api"), Service("a", "b", port: 80)],
            []);

        var g = ArchitectureBuilder.Build(inputs, ["a"], ArchOverlay.Empty);

        g.Edges.Should().ContainSingle();
        g.Edges[0].Source.Should().Be("env B1");
    }

    [Fact]
    public void Dismissed_edges_are_hidden_and_counted()
    {
        var dismissed = ArchEdge.MakeId(Cart, CartRedis);

        var g = Build(new ArchOverlay([], [dismissed, "w:gone->w:gone2"]));

        g.Edges.Should().NotContain(e => e.Id == dismissed);
        g.Dismissed.Should().Be(1);
    }

    [Fact]
    public void Manual_edges_are_added_including_new_external_hosts()
    {
        var overlay = new ArchOverlay(
            [
                new ManualEdge(Gateway, "x:api.stripe.com", ArchEdgeKind.Http, "HTTPS"),
                new ManualEdge(Settlement, Gateway, ArchEdgeKind.Http, null),
                new ManualEdge(W("nope", "gone"), Gateway, ArchEdgeKind.Http, null),
            ],
            []);

        var g = Build(overlay);

        Node(g, "x:api.stripe.com").Should().Match<ArchNode>(n => n.Kind == ArchNodeKind.External && n.Name == "api.stripe.com");
        Edge(g, Gateway, "x:api.stripe.com").Should().Match<ArchEdge>(e => e.Manual && e.Source == "manual" && e.Label == "HTTPS");
        Edge(g, Settlement, Gateway).Label.Should().Be("HTTP");
        g.Edges.Should().NotContain(e => e.From == W("nope", "gone"), "an edge from a node that isn't on the map is skipped");
    }

    [Fact]
    public void A_manual_edge_that_duplicates_a_detected_one_is_ignored()
    {
        var g = Build(new ArchOverlay([new ManualEdge(Cart, CartRedis, ArchEdgeKind.Http, "x")], []));

        Edge(g, Cart, CartRedis).Manual.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://svc:8080/path?q=1", "svc", 8080, "http")]
    [InlineData("https://user:p@ss@api.example.com/v1", "api.example.com", null, "https")]
    [InlineData("svc.ns.svc.cluster.local:9092", "svc.ns.svc.cluster.local", 9092, null)]
    [InlineData("kafka-0.kafka.platform.svc", "kafka-0.kafka.platform.svc", null, null)]
    [InlineData("Server=tcp:db.corp,1433;User Id=x", "db.corp", 1433, null)]
    [InlineData("Host=pg;Port=5432;Password=x", "pg", 5432, null)]
    public void Endpoints_parse_common_value_shapes(string value, string host, int? port, string? scheme)
    {
        ArchitectureBuilder.Endpoints(value).Should().ContainSingle()
            .Which.Should().Be(new ArchitectureBuilder.Endpoint(host, port, scheme));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("true")]
    [InlineData("/var/lib/data")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("12:30")]
    public void Plain_values_yield_no_endpoint(string value)
    {
        ArchitectureBuilder.Endpoints(value).Should().BeEmpty();
    }

    [Fact]
    public void Comma_separated_broker_lists_yield_each_host()
    {
        ArchitectureBuilder.Endpoints("kafka-0:9092,kafka-1:9092").Select(e => e.Host).Should().Equal("kafka-0", "kafka-1");
    }
}
