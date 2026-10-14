using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class KubeconfigSettingsTests
{
    [Fact]
    public void Round_trips_through_ToSecretString()
    {
        var yaml = TestData.Kubeconfig();
        var secret = new KubeconfigSettings(yaml, "dev").ToSecretString();

        var parsed = KubeconfigSettings.Parse(secret);

        parsed.Kubeconfig.Should().Be(yaml);
        parsed.Context.Should().Be("dev");
        parsed.Summary.Host.Should().Be("k.example");
        parsed.Summary.CredentialKind.Should().Be("token");
    }

    [Fact]
    public void Parse_rejects_bad_json()
    {
        var act = () => KubeconfigSettings.Parse("not json");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_rejects_missing_fields()
    {
        var act = () => KubeconfigSettings.Parse("""{"kubeconfig":"x"}""");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_rejects_an_exec_kubeconfig()
    {
        var secret = new KubeconfigSettings(TestData.Kubeconfig("exec:\n  command: aws"), "dev").ToSecretString();

        var act = () => KubeconfigSettings.Parse(secret);

        act.Should().Throw<FormatException>().WithMessage("*exec*");
    }

    [Fact]
    public void ToSecretString_does_not_reformat_the_yaml()
    {
        var yaml = TestData.Kubeconfig() + "\n# a comment   \n";

        var parsed = KubeconfigSettings.Parse(new KubeconfigSettings(yaml, "dev").ToSecretString());

        parsed.Kubeconfig.Should().Be(yaml);
    }

    [Fact]
    public void Secret_uses_camel_case_property_names()
    {
        new KubeconfigSettings("y", "c").ToSecretString().Should().Be("""{"kubeconfig":"y","context":"c"}""");
    }
}
