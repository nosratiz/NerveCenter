using FluentAssertions;
using k8s.Models;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class YamlDescribeTests
{
    [Fact]
    public void Managed_fields_are_stripped()
    {
        var deployment = new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta
            {
                Name = "web",
                NamespaceProperty = "checkout",
                ManagedFields = [new V1ManagedFieldsEntry { Manager = "kubectl", Operation = "Apply" }],
            },
            Spec = new V1DeploymentSpec { Replicas = 2 },
        };

        var yaml = YamlDescribe.Serialize(deployment);

        yaml.Should().Contain("name: web").And.Contain("replicas: 2").And.Contain("kind: Deployment");
        yaml.Should().NotContain("managedFields").And.NotContain("kubectl");
    }

    [Fact]
    public void An_object_without_metadata_serializes()
    {
        YamlDescribe.Serialize(new V1ConfigMap { Data = new Dictionary<string, string> { ["k"] = "v" } })
            .Should().Contain("k: v");
    }

    [Fact]
    public void Secrets_are_never_serialized()
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = "db" },
            StringData = new Dictionary<string, string> { ["password"] = "hunter2" },
        };

        FluentActions.Invoking(() => YamlDescribe.Serialize(secret))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Secrets are never serialized.");
    }
}
