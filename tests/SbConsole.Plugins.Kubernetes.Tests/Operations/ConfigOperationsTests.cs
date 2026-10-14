using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

namespace SbConsole.Plugins.Kubernetes.Tests.Operations;

[Collection("k3s")]
/// <summary>Objects live in their own namespace; each test removes what it created.</summary>
public class ConfigOperationsTests(K3sFixture k3s)
{
    private const string Ns = "sbc-config";
    private const string SecretValue = "s3cr3t-value-xyz";
    private static readonly string[] Scope = [Ns];

    [DockerFact]
    public async Task Listing_secrets_returns_metadata_and_never_a_value()
    {
        const string name = "sbc-opaque";
        await k3s.EnsureNamespaceAsync(Ns);
        await k3s.Admin.CoreV1.CreateNamespacedSecretAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = name },
            StringData = new Dictionary<string, string> { ["PASSWORD"] = SecretValue, ["API_KEY"] = SecretValue + "-2" },
        }, Ns);
        try
        {
            var secrets = await k3s.Operations.ListSecretsAsync(k3s.AdminSecret, Scope);

            var summary = secrets.Should().ContainSingle(s => s.Name == name).Subject;
            summary.Namespace.Should().Be(Ns);
            summary.Type.Should().Be("Opaque");
            summary.KeyNames.Should().Equal("API_KEY", "PASSWORD");
            summary.TlsNotAfter.Should().BeNull();
            var json = JsonSerializer.Serialize(secrets);
            json.Should().NotContain(SecretValue);
            json.Should().NotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(SecretValue)));
        }
        finally
        {
            await Ignore404(() => k3s.Admin.CoreV1.DeleteNamespacedSecretAsync(name, Ns));
        }
    }

    [DockerFact]
    public async Task A_tls_secret_reports_its_certificate_expiry()
    {
        const string name = "sbc-tls";
        await k3s.EnsureNamespaceAsync(Ns);
        var notAfter = DateTimeOffset.UtcNow.AddDays(9);
        var (certPem, keyPem) = SelfSigned(notAfter);
        await k3s.Admin.CoreV1.CreateNamespacedSecretAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = name },
            Type = "kubernetes.io/tls",
            StringData = new Dictionary<string, string> { ["tls.crt"] = certPem, ["tls.key"] = keyPem },
        }, Ns);
        try
        {
            var secrets = await k3s.Operations.ListSecretsAsync(k3s.AdminSecret, Scope);

            var tls = secrets.Single(s => s.Name == name);
            tls.Type.Should().Be("kubernetes.io/tls");
            tls.KeyNames.Should().Equal("tls.crt", "tls.key");
            tls.TlsNotAfter.Should().NotBeNull();
            tls.TlsNotAfter!.Value.Should().BeCloseTo(notAfter, TimeSpan.FromMinutes(1));
            tls.TlsNotAfter.Value.Offset.Should().Be(TimeSpan.Zero);
            JsonSerializer.Serialize(secrets).Should().NotContain("PRIVATE KEY").And.NotContain("BEGIN CERTIFICATE");
        }
        finally
        {
            await Ignore404(() => k3s.Admin.CoreV1.DeleteNamespacedSecretAsync(name, Ns));
        }
    }

    [DockerFact]
    public async Task A_tls_secret_with_an_unparseable_certificate_has_no_expiry()
    {
        const string name = "sbc-tls-bad";
        await k3s.EnsureNamespaceAsync(Ns);
        await k3s.Admin.CoreV1.CreateNamespacedSecretAsync(new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = name },
            Type = "kubernetes.io/tls",
            StringData = new Dictionary<string, string> { ["tls.crt"] = "not a certificate " + SecretValue, ["tls.key"] = "nope" },
        }, Ns);
        try
        {
            var secrets = await k3s.Operations.ListSecretsAsync(k3s.AdminSecret, Scope);

            secrets.Single(s => s.Name == name).TlsNotAfter.Should().BeNull();
            JsonSerializer.Serialize(secrets).Should().NotContain(SecretValue);
        }
        finally
        {
            await Ignore404(() => k3s.Admin.CoreV1.DeleteNamespacedSecretAsync(name, Ns));
        }
    }

    [DockerFact]
    public async Task Viewer_listing_secrets_is_forbidden_with_the_friendly_sentence()
    {
        var viewer = await k3s.ViewerSecretAsync();

        var act = () => k3s.Operations.ListSecretsAsync(viewer, []);

        var ex = (await act.Should().ThrowAsync<HttpOperationException>()).Which;
        ex.Response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        FriendlyKubernetesError.From(ex).Should().Be("Your account can't list secrets.");
    }

    [Fact]
    public async Task A_non_http_failure_listing_secrets_is_replaced_by_a_contentless_exception()
    {
        using var factory = new KubernetesClientFactory();
        var operations = new KubernetesOperations(factory, TimeProvider.System, NullLogger<KubernetesOperations>.Instance);
        var unreachable = new KubeconfigSettings(TestData.Kubeconfig(server: "https://127.0.0.1:1", skipTls: true), "dev").ToSecretString();

        var act = () => operations.ListSecretsAsync(unreachable, []);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Be("Listing secrets failed.");
        ex.InnerException.Should().BeNull();
    }

    [DockerFact]
    public async Task A_config_map_used_by_a_deployment_via_env_and_volume_has_both_refs()
    {
        const string name = "sbc-cfg-user";
        await k3s.EnsureNamespaceAsync(Ns);
        await k3s.Admin.CoreV1.CreateNamespacedConfigMapAsync(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = name + "-cm" },
            Data = new Dictionary<string, string> { ["MODE"] = "prod", ["app.yaml"] = "a: 1\nb: 2\n" },
            BinaryData = new Dictionary<string, byte[]> { ["logo.png"] = new byte[300] },
        }, Ns);
        await k3s.ApplyAsync(new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = Ns },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app"] = name } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { ["app"] = name } },
                    Spec = new V1PodSpec
                    {
                        TerminationGracePeriodSeconds = 0,
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "app", Image = "busybox:1.36", Command = ["sleep", "3600"],
                                Env = [new V1EnvVar { Name = "MODE", ValueFrom = new V1EnvVarSource { ConfigMapKeyRef = new V1ConfigMapKeySelector { Name = name + "-cm", Key = "MODE" } } }],
                                VolumeMounts = [new V1VolumeMount { Name = "cfg", MountPath = "/etc/app" }],
                            },
                        ],
                        Volumes = [new V1Volume { Name = "cfg", ConfigMap = new V1ConfigMapVolumeSource { Name = name + "-cm" } }],
                    },
                },
            },
        });
        try
        {
            await K3sFixture.WaitAsync(async () =>
                (await k3s.Admin.CoreV1.ListNamespacedPodAsync(Ns, labelSelector: $"app={name}")).Items.Count > 0, TimeSpan.FromMinutes(1));

            var data = await k3s.Operations.ListConfigMapsAsync(k3s.AdminSecret, Scope);

            var map = data.ConfigMaps.Single(m => m.Name == name + "-cm");
            map.Namespace.Should().Be(Ns);
            map.Data.Should().BeEquivalentTo(new Dictionary<string, string> { ["MODE"] = "prod", ["app.yaml"] = "a: 1\nb: 2\n" });
            map.BinarySizes.Should().BeEquivalentTo(new Dictionary<string, long> { ["logo.png"] = 300 });
            map.ResourceVersion.Should().NotBeNullOrEmpty();
            data.Refs.Where(r => r.ConfigMap == name + "-cm").Should().BeEquivalentTo(
            [
                new K8sConfigRef(Ns, name + "-cm", name, "env"),
                new K8sConfigRef(Ns, name + "-cm", name, "volume"),
            ]);
            data.Refs.Should().Contain(new K8sConfigRef(Ns, "kube-root-ca.crt", name, "projected"));
            data.ConfigMaps.Should().OnlyContain(m => m.Namespace == Ns);
        }
        finally
        {
            await Ignore404(() => k3s.Admin.AppsV1.DeleteNamespacedDeploymentAsync(name, Ns, propagationPolicy: "Background"));
            await Ignore404(() => k3s.Admin.CoreV1.DeleteNamespacedConfigMapAsync(name + "-cm", Ns));
        }
    }

    [DockerFact]
    public async Task Update_replaces_the_data_and_keeps_binary_data()
    {
        const string name = "sbc-cfg-update";
        await k3s.EnsureNamespaceAsync(Ns);
        var created = await k3s.Admin.CoreV1.CreateNamespacedConfigMapAsync(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = name },
            Data = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
            BinaryData = new Dictionary<string, byte[]> { ["blob"] = [1, 2, 3] },
        }, Ns);
        try
        {
            await k3s.Operations.UpdateConfigMapAsync(k3s.AdminSecret, Ns, name,
                new Dictionary<string, string> { ["A"] = "10", ["C"] = "3" }, created.Metadata.ResourceVersion);

            var read = await k3s.Admin.CoreV1.ReadNamespacedConfigMapAsync(name, Ns);
            read.Data.Should().BeEquivalentTo(new Dictionary<string, string> { ["A"] = "10", ["C"] = "3" });
            read.BinaryData.Should().ContainKey("blob");
        }
        finally
        {
            await Ignore404(() => k3s.Admin.CoreV1.DeleteNamespacedConfigMapAsync(name, Ns));
        }
    }

    [DockerFact]
    public async Task Update_with_a_stale_resource_version_is_a_conflict()
    {
        const string name = "sbc-cfg-stale";
        await k3s.EnsureNamespaceAsync(Ns);
        var created = await k3s.Admin.CoreV1.CreateNamespacedConfigMapAsync(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = name },
            Data = new Dictionary<string, string> { ["A"] = "1" },
        }, Ns);
        try
        {
            // Someone else edits it after we loaded it.
            created.Data["A"] = "someone-else";
            await k3s.Admin.CoreV1.ReplaceNamespacedConfigMapAsync(created, name, Ns);

            var act = () => k3s.Operations.UpdateConfigMapAsync(k3s.AdminSecret, Ns, name,
                new Dictionary<string, string> { ["A"] = "mine" }, created.Metadata.ResourceVersion);

            var ex = (await act.Should().ThrowAsync<HttpOperationException>()).Which;
            ex.Response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            FriendlyKubernetesError.From(ex).Should().Be("It changed since you loaded it — reload and try again.");
            (await k3s.Admin.CoreV1.ReadNamespacedConfigMapAsync(name, Ns)).Data["A"].Should().Be("someone-else");
        }
        finally
        {
            await Ignore404(() => k3s.Admin.CoreV1.DeleteNamespacedConfigMapAsync(name, Ns));
        }
    }

    private static (string Cert, string Key) SelfSigned(DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=checkout.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter);
        return (cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    private static async Task Ignore404(Func<Task> delete)
    {
        try
        {
            await delete();
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            // Already gone.
        }
    }
}
