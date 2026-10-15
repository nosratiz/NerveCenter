using System.Net;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Config;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Config;

public class ConfigHandlerTests
{
    private const string Secret = "secret";
    private const string Changed = "It changed since you loaded it — reload and try again.";
    private const string DistinctiveValue = "zebra-quartz-7731";
    private readonly IKubernetesOperations _ops = Substitute.For<IKubernetesOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly Guid _id = Guid.NewGuid();
    private static readonly string[] Checkout = ["checkout"];

    public ConfigHandlerTests() =>
        _connections.GetSecretAsync(_id, Arg.Any<CancellationToken>()).Returns(Secret);

    private K8sTarget Target => new(_id, "aks-prod-eu", "checkout", "gateway-config");

    private ListConfigMapsQueryHandler ListMaps() => new(_ops, _connections, NullLogger<ListConfigMapsQueryHandler>.Instance);

    private ListSecretsQueryHandler ListSecrets() => new(_ops, _connections, NullLogger<ListSecretsQueryHandler>.Instance);

    private UpdateConfigMapCommandHandler Update() => new(_ops, _connections, _audit, NullLogger<UpdateConfigMapCommandHandler>.Instance);

    private static HttpOperationException Http(HttpStatusCode code, string message = "") => new("raw")
    {
        Response = new HttpResponseMessageWrapper(new HttpResponseMessage(code), $"{{\"kind\":\"Status\",\"message\":\"{message}\"}}"),
    };

    private static Dictionary<string, string> D(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public async Task List_config_maps_resolves_usage()
    {
        _ops.ListConfigMapsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(TestData.ConfigData());

        var result = await ListMaps().HandleAsync(_id, Checkout);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(r => r.Map.Name).Should().Equal("gateway-config", "kube-root-ca.crt", "reports-config");
        result.Value!.Single(r => r.Map.Name == "gateway-config").UsedBy.Single().Via.Should().Equal("env", "volume");
        await _ops.Received(1).ListConfigMapsAsync(Secret, Arg.Is<IReadOnlyList<string>>(n => n.SequenceEqual(Checkout)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_secrets_returns_the_summaries()
    {
        _ops.ListSecretsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(TestData.Secrets());

        var result = await ListSecrets().HandleAsync(_id, Checkout);

        result.Value.Should().BeEquivalentTo(TestData.Secrets());
    }

    [Fact]
    public async Task A_forbidden_secrets_list_reads_your_account_cant_list_secrets()
    {
        _ops.ListSecretsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).ThrowsAsync(Http(HttpStatusCode.Forbidden,
            "secrets is forbidden: User \\\"system:serviceaccount:sbc-test:viewer\\\" cannot list resource \\\"secrets\\\" in API group \\\"\\\" at the cluster scope"));

        var result = await ListSecrets().HandleAsync(_id, []);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Your account can't list secrets.");
    }

    [Fact]
    public async Task Update_sends_the_new_data_and_resource_version_and_audits_key_names_only()
    {
        var original = D(("KEEP", "same"), ("OLD", "gone-value"), ("TTL", "900"));
        var data = D(("KEEP", "same"), ("TTL", DistinctiveValue), ("NEW_B", DistinctiveValue), ("NEW_A", "x"));

        var result = await Update().HandleAsync(new UpdateConfigMapCommand(Target, original, data, "1001"));

        result.IsSuccess.Should().BeTrue();
        await _ops.Received(1).UpdateConfigMapAsync(Secret, "checkout", "gateway-config",
            Arg.Is<IReadOnlyDictionary<string, string>>(d => d.Count == 4 && d["TTL"] == DistinctiveValue), "1001", Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("k8s.configmap.update", "aks-prod-eu/checkout/gateway-config", ActionRisk.Mutating, true,
            "added NEW_A, NEW_B · changed TTL · removed OLD", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_audit_detail_never_contains_a_value()
    {
        string? detail = null;
        await _audit.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActionRisk>(), Arg.Any<bool>(),
            Arg.Do<string?>(d => detail = d), Arg.Any<CancellationToken>());

        await Update().HandleAsync(new UpdateConfigMapCommand(Target, D(("A", "old")), D(("A", DistinctiveValue), ("B", DistinctiveValue)), "1"));

        detail.Should().NotBeNull().And.NotContain(DistinctiveValue).And.NotContain("old");
    }

    [Fact]
    public async Task Nothing_changed_fails_without_calling_the_cluster_or_auditing()
    {
        var result = await Update().HandleAsync(new UpdateConfigMapCommand(Target, D(("A", "1")), D(("A", "1")), "1"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Nothing changed.");
        await _ops.DidNotReceiveWithAnyArgs().UpdateConfigMapAsync(default!, default!, default!, default!, default!, default);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_conflict_reads_the_changed_since_sentence_and_is_audited_as_failed()
    {
        _ops.UpdateConfigMapAsync(Secret, "checkout", "gateway-config", Arg.Any<IReadOnlyDictionary<string, string>>(), "1001", Arg.Any<CancellationToken>())
            .ThrowsAsync(Http(HttpStatusCode.Conflict, "the object has been modified"));

        var result = await Update().HandleAsync(new UpdateConfigMapCommand(Target, D(("A", "1")), D(("A", "2")), "1001"));

        result.Error.Should().Be(Changed);
        await _audit.Received(1).RecordAsync("k8s.configmap.update", "aks-prod-eu/checkout/gateway-config", ActionRisk.Mutating, false,
            Changed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_is_not_audited()
    {
        var unknown = Guid.NewGuid();
        _connections.GetSecretAsync(unknown, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Update().HandleAsync(new UpdateConfigMapCommand(Target with { ConnectionId = unknown }, D(("A", "1")), D(("A", "2")), "1"));

        result.Error.Should().Be("Connection not found.");
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }
}
