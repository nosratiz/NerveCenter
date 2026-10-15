using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Architecture;

/// <summary>The mockup 4a cluster: checkout + payments, with redis, kafka and postgres as stores.</summary>
internal static class ArchTestData
{
    public static K8sArchWorkload Workload(
        string ns, string name, K8sWorkloadKind kind = K8sWorkloadKind.Deployment, int desired = 2, int ready = 2,
        bool unhealthy = false, string? image = null, IReadOnlyDictionary<string, string>? labels = null,
        params (string Name, string? Value)[] env) =>
        new(kind, ns, name, desired, ready, unhealthy,
            labels ?? new Dictionary<string, string>(),
            new Dictionary<string, string> { ["app"] = name },
            [image ?? $"ghcr.io/nervecenter/{name}:1.0.0"],
            env.Select(e => new K8sArchEnvVar(e.Name, e.Value)).ToList());

    public static K8sArchService Service(string ns, string name, string? app = null, int port = 8080, string? externalName = null) =>
        new(ns, name,
            externalName is null ? new Dictionary<string, string> { ["app"] = app ?? name } : new Dictionary<string, string>(),
            [port], externalName);

    public static K8sArchitectureInputs Cluster() => new(
        [
            Workload("checkout", "checkout-api", desired: 6, ready: 6, labels: new Dictionary<string, string> { ["team"] = "storefront" },
                env: [("PAYMENTS_URL", "http://payment-gateway:8080"), ("CART_URL", "http://cart.checkout.svc.cluster.local:8080"), ("LOG_LEVEL", "info")]),
            Workload("checkout", "payment-gateway", desired: 4, ready: 2, unhealthy: true, image: "ghcr.io/nervecenter/payment-gateway:2.14.1",
                labels: new Dictionary<string, string> { ["team"] = "payments" },
                env: [("REDIS_URL", "redis://:hunter2@cart-redis:6379/0"), ("KAFKA_BROKERS", "kafka.platform:9092"), ("STRIPE_KEY", null)]),
            Workload("checkout", "cart", desired: 3, ready: 3, env: [("REDIS_HOST", "cart-redis:6379")]),
            Workload("checkout", "pricing", env: [("DB", "Host=pg-main.data;Port=5432;Username=pricing;Password=s3cret")]),
            Workload("checkout", "cart-redis", K8sWorkloadKind.StatefulSet, 1, 1, image: "redis:7.2-alpine"),
            Workload("payments", "settlement-api", desired: 4, ready: 4, env: [("LEDGER_URL", "http://ledger:8080")]),
            Workload("payments", "ledger", desired: 3, ready: 3,
                env: [("DATABASE_URL", "postgres://ledger:pw@pg-main.data.svc:5432/ledger"), ("EVENTS", "kafka.platform.svc.cluster.local:9092")]),
            Workload("payments", "webhooks", desired: 0, ready: 0, env: [("HOOK_TARGET", "https://hooks.example.com/in")]),
            Workload("platform", "kafka", K8sWorkloadKind.StatefulSet, 3, 3, image: "bitnami/kafka:3.7"),
            Workload("data", "pg-main", K8sWorkloadKind.StatefulSet, 1, 1, image: "postgres:16"),
            Workload("platform", "unrelated", env: [("X", "http://nowhere:80")]),
        ],
        [
            Service("checkout", "checkout-api"), Service("checkout", "payment-gateway"), Service("checkout", "cart"),
            Service("checkout", "pricing"), Service("checkout", "cart-redis", port: 6379),
            Service("payments", "settlement-api"), Service("payments", "ledger"),
            Service("platform", "kafka", port: 9092), Service("data", "pg-main", port: 5432),
        ],
        [new K8sArchIngress("checkout", "shop", ["shop.example.com", "api.example.com"], ["checkout-api"])]);

    public static readonly string[] Scope = ["checkout", "payments"];

    public static string W(string ns, string name, K8sWorkloadKind kind = K8sWorkloadKind.Deployment) => $"w:{ns}/{kind}/{name}";
}
