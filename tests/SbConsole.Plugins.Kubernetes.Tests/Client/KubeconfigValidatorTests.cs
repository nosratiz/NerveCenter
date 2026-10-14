using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class KubeconfigValidatorTests
{
    private const string ExecSentence =
        "This user signs in by running a program (exec). SbConsole only accepts static credentials — use a ServiceAccount token (see docker/README.md › Kubernetes).";

    [Fact]
    public void Token_user_is_valid()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig(), null);

        r.Errors.Should().BeEmpty();
        r.Summary.Should().NotBeNull();
        r.Summary!.Host.Should().Be("k.example");
        r.Summary.Server.Should().Be("https://k.example:6443");
        r.Summary.Context.Should().Be("dev");
        r.Summary.User.Should().Be("u1");
        r.Summary.CredentialKind.Should().Be("token");
        r.Summary.TlsVerified.Should().BeTrue();
    }

    [Fact]
    public void Client_certificate_data_user_is_valid()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig("client-certificate-data: QUJD\nclient-key-data: REVG"), null);

        r.Errors.Should().BeEmpty();
        r.Summary!.CredentialKind.Should().Be("client-certificate");
    }

    [Fact]
    public void Basic_auth_user_is_valid()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig("username: bob\npassword: pw"), null);

        r.Errors.Should().BeEmpty();
        r.Summary!.CredentialKind.Should().Be("basic");
    }

    [Fact]
    public void Exec_user_is_rejected_with_the_exec_sentence()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig("exec:\n  apiVersion: client.authentication.k8s.io/v1\n  command: aws"), null);

        r.Errors.Should().Contain(ExecSentence);
        r.Summary.Should().BeNull();
    }

    [Fact]
    public void Auth_provider_user_is_rejected()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig("auth-provider:\n  name: gcp"), null);

        r.Errors.Should().Contain(ExecSentence.Replace("(exec)", "(auth-provider)"));
    }

    [Fact]
    public void Certificate_authority_path_is_rejected()
    {
        var yaml = TestData.Kubeconfig().Replace("insecure-skip-tls-verify: false", "certificate-authority: /etc/ca.crt");

        KubeconfigValidator.Validate(yaml, null).Errors.Should().Contain(
            "certificate-authority points at a file on the SbConsole server; paste it as certificate-authority-data instead.");
    }

    [Fact]
    public void Client_certificate_path_is_rejected()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig("client-certificate: /a.crt\nclient-key: /a.key"), null);

        r.Errors.Should().Contain("client-certificate / client-key point at files; use client-certificate-data / client-key-data.");
    }

    [Fact]
    public void No_credential_is_rejected()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig("{}"), null);

        r.Errors.Should().Contain("User 'u1' has no static credential (token, client certificate data, or username/password).");
    }

    [Fact]
    public void Two_credentials_are_rejected()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig("token: abc\nusername: bob\npassword: pw"), null);

        r.Errors.Should().Contain("User 'u1' has more than one credential; keep exactly one.");
    }

    [Fact]
    public void Unknown_context_is_rejected()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig(), "nope");

        r.Errors.Should().Contain("Context 'nope' isn't in this kubeconfig.");
        r.Contexts.Should().Equal("dev");
    }

    [Fact]
    public void Missing_cluster_entry_is_rejected()
    {
        var yaml = TestData.Kubeconfig().Replace("cluster: c1", "cluster: ghost");

        KubeconfigValidator.Validate(yaml, null).Errors.Should().Contain(
            "Context 'dev' points at cluster 'ghost', which isn't defined.");
    }

    [Fact]
    public void Missing_user_entry_is_rejected()
    {
        var yaml = TestData.Kubeconfig().Replace("user: u1", "user: ghost");

        KubeconfigValidator.Validate(yaml, null).Errors.Should().Contain(
            "Context 'dev' points at user 'ghost', which isn't defined.");
    }

    [Theory]
    [InlineData("http://k.example:8080", false)]
    [InlineData("k.example", false)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("https://k.example", true)]
    public void Http_server_is_rejected_except_localhost(string server, bool valid)
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig(server: server), null);

        if (valid)
        {
            r.Errors.Should().BeEmpty();
        }
        else
        {
            r.Errors.Should().Contain("The cluster server must be an https:// URL.");
        }
    }

    [Fact]
    public void Insecure_skip_tls_is_accepted_and_reported()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig(skipTls: true), null);

        r.Errors.Should().BeEmpty();
        r.Summary!.TlsVerified.Should().BeFalse();
    }

    [Fact]
    public void Oversized_input_is_rejected()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig() + "\n# " + new string('x', KubeconfigValidator.MaxBytes), null);

        r.Errors.Should().Equal("The kubeconfig is larger than 256 KB.");
    }

    [Theory]
    [InlineData("not: [valid")]
    [InlineData("\t\t::: garbage")]
    [InlineData("")]
    public void Garbage_yaml_is_rejected_without_throwing(string yaml)
    {
        var r = KubeconfigValidator.Validate(yaml, null);

        r.Errors.Should().NotBeEmpty();
        r.Summary.Should().BeNull();
    }

    [Fact]
    public void Null_context_uses_current_context()
    {
        var r = KubeconfigValidator.Validate(TestData.Kubeconfig(context: "prod-eu"), null);

        r.CurrentContext.Should().Be("prod-eu");
        r.Summary!.Context.Should().Be("prod-eu");
    }

    [Fact]
    public void Lists_all_contexts_in_file_order()
    {
        var yaml = """
            apiVersion: v1
            kind: Config
            current-context: zeta
            clusters:
            - name: c1
              cluster: { server: "https://k.example:6443" }
            contexts:
            - name: zeta
              context: { cluster: c1, user: u1 }
            - name: alpha
              context: { cluster: c1, user: u1 }
            users:
            - name: u1
              user: { token: abc }
            """;

        var r = KubeconfigValidator.Validate(yaml, "alpha");

        r.Contexts.Should().Equal("zeta", "alpha");
        r.Errors.Should().BeEmpty();
        r.Summary!.Context.Should().Be("alpha");
    }
}
