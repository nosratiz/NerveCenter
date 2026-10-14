using System.Text;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SbConsole.Plugins.Kubernetes.Client;
using Testcontainers.K3s;

namespace SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

[CollectionDefinition("k3s")]
public sealed class K3sCollection : ICollectionFixture<K3sFixture>;

/// <summary>One k3s container per test run. Tests seed through <see cref="Admin"/> and exercise <see cref="Operations"/>.</summary>
public sealed class K3sFixture : IAsyncLifetime
{
    public const string Image = "rancher/k3s:v1.30.6-k3s1";
    public const string TestNamespace = "sbc-test";

    private K3sContainer? _container;
    private k8s.Kubernetes? _admin;
    private string? _adminSecret;
    private string? _viewerSecret;
    private readonly SemaphoreSlim _viewerLock = new(1, 1);
    private readonly KubernetesClientFactory _factory = new();

    public KubernetesOperations Operations { get; }

    public K3sFixture() =>
        Operations = new KubernetesOperations(_factory, TimeProvider.System, NullLogger<KubernetesOperations>.Instance);

    public k8s.Kubernetes Admin => _admin ?? throw new InvalidOperationException("Docker is not available.");

    public string AdminSecret => _adminSecret ?? throw new InvalidOperationException("Docker is not available.");

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        _container = new K3sBuilder(Image).Build();
        await _container.StartAsync();

        var kubeconfig = await _container.GetKubeconfigAsync();
        var context = KubeconfigValidator.Validate(kubeconfig, null).CurrentContext
            ?? throw new InvalidOperationException("k3s kubeconfig has no current-context.");
        _adminSecret = new KubeconfigSettings(kubeconfig, context).ToSecretString();
        _admin = new k8s.Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(
            new MemoryStream(Encoding.UTF8.GetBytes(kubeconfig)), context));

        // The API server answers before the default ServiceAccount exists; wait so seeding never races it.
        await WaitAsync(async () =>
        {
            var sas = await Admin.CoreV1.ListNamespacedServiceAccountAsync("default");
            return sas.Items.Any(s => s.Metadata.Name == "default");
        }, TimeSpan.FromMinutes(2));
    }

    public async Task DisposeAsync()
    {
        _admin?.Dispose();
        _factory.Dispose();
        _viewerLock.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>A token secret for ServiceAccount sbc-test/viewer, bound to the built-in "view" ClusterRole.</summary>
    public async Task<string> ViewerSecretAsync()
    {
        await _viewerLock.WaitAsync();
        try
        {
            if (_viewerSecret is not null)
            {
                return _viewerSecret;
            }

            await EnsureNamespaceAsync(TestNamespace);
            await IgnoreConflict(() => Admin.CoreV1.CreateNamespacedServiceAccountAsync(
                new V1ServiceAccount { Metadata = new V1ObjectMeta { Name = "viewer" } }, TestNamespace));
            await IgnoreConflict(() => Admin.RbacAuthorizationV1.CreateClusterRoleBindingAsync(new V1ClusterRoleBinding
            {
                Metadata = new V1ObjectMeta { Name = "sbc-viewer" },
                RoleRef = new V1RoleRef { ApiGroup = "rbac.authorization.k8s.io", Kind = "ClusterRole", Name = "view" },
                Subjects = [new Rbacv1Subject { Kind = "ServiceAccount", Name = "viewer", NamespaceProperty = TestNamespace }],
            }));
            var token = await Admin.CoreV1.CreateNamespacedServiceAccountTokenAsync(
                new Authenticationv1TokenRequest { Spec = new V1TokenRequestSpec { ExpirationSeconds = 3600 } },
                "viewer", TestNamespace);
            _viewerSecret = TokenSecret(token.Status.Token);
            return _viewerSecret;
        }
        finally
        {
            _viewerLock.Release();
        }
    }

    /// <summary>A secret with the admin cluster's server and CA but a static bearer token.</summary>
    public string TokenSecret(string token)
    {
        var admin = KubeconfigSettings.Parse(AdminSecret);
        var config = KubernetesClientConfiguration.LoadKubeConfig(new MemoryStream(Encoding.UTF8.GetBytes(admin.Kubeconfig)));
        var cluster = config.Clusters.Single(c => c.Name == config.Contexts.Single(x => x.Name == admin.Context).ContextDetails.Cluster);
        var yaml = $"""
            apiVersion: v1
            kind: Config
            current-context: token
            clusters:
            - name: k3s
              cluster:
                server: {cluster.ClusterEndpoint.Server}
                certificate-authority-data: {cluster.ClusterEndpoint.CertificateAuthorityData}
            contexts:
            - name: token
              context:
                cluster: k3s
                user: token
            users:
            - name: token
              user:
                token: {token}
            """;
        return new KubeconfigSettings(yaml, "token").ToSecretString();
    }

    public Task EnsureNamespaceAsync(string name) =>
        IgnoreConflict(() => Admin.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = name } }));

    /// <summary>Creates the Deployment, or replaces it when it already exists.</summary>
    public async Task<V1Deployment> ApplyAsync(V1Deployment deployment)
    {
        var ns = deployment.Metadata.NamespaceProperty ?? "default";
        await EnsureNamespaceAsync(ns);
        try
        {
            return await Admin.AppsV1.CreateNamespacedDeploymentAsync(deployment, ns);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 409)
        {
            var existing = await Admin.AppsV1.ReadNamespacedDeploymentAsync(deployment.Metadata.Name, ns);
            deployment.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            return await Admin.AppsV1.ReplaceNamespacedDeploymentAsync(deployment, deployment.Metadata.Name, ns);
        }
    }

    /// <summary>Polls <paramref name="condition"/> every 500 ms until it is true or <paramref name="timeout"/> passes.</summary>
    public static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (HttpOperationException) when (DateTime.UtcNow < deadline)
            {
                // Not there yet.
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Condition not met within {timeout}.");
            }

            await Task.Delay(500);
        }
    }

    private static async Task IgnoreConflict(Func<Task> create)
    {
        try
        {
            await create();
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 409)
        {
            // Already exists.
        }
    }
}
