using FluentAssertions;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class RabbitConnectionSettingsTests
{
    [Fact]
    public void Plain_defaults_when_only_required_keys_are_set()
    {
        var s = RabbitConnectionSettings.From("host=rabbit;username=u;password=p");

        s.Host.Should().Be("rabbit");
        s.Tls.Should().BeFalse();
        s.AmqpPort.Should().Be(5672);
        s.ManagementBaseUri.Should().Be(new Uri("http://rabbit:15672/"));
        s.Vhost.Should().Be("/");
        s.Username.Should().Be("u");
        s.Password.Should().Be("p");
        s.VerifyCert.Should().BeTrue();
    }

    [Fact]
    public void Tls_defaults_switch_both_ports_and_the_management_scheme()
    {
        var s = RabbitConnectionSettings.From("host=rabbit;username=u;password=p;tls=true");

        s.Tls.Should().BeTrue();
        s.AmqpPort.Should().Be(5671);
        s.ManagementBaseUri.Should().Be(new Uri("https://rabbit:15671/"));
        s.VerifyCert.Should().BeTrue();
    }

    [Fact]
    public void Explicit_values_override_every_default()
    {
        var secret = RabbitConfigParser.Serialize(new Dictionary<string, string>
        {
            ["host"] = "rabbit",
            ["amqpPort"] = "15000",
            ["managementUrl"] = "https://mgmt.example/rabbitmq",
            ["vhost"] = "/orders",
            ["username"] = "u",
            ["password"] = "p;=%",
            ["tls"] = "TRUE",
            ["verifyCert"] = "false",
        });

        var s = RabbitConnectionSettings.From(secret);

        s.AmqpPort.Should().Be(15000);
        s.ManagementBaseUri.Should().Be(new Uri("https://mgmt.example/rabbitmq/"));
        s.Vhost.Should().Be("/orders");
        s.Password.Should().Be("p;=%");
        s.Tls.Should().BeTrue();
        s.VerifyCert.Should().BeFalse();
    }

    [Theory]
    [InlineData("username=u;password=p", "host")]
    [InlineData("host=h;password=p", "username")]
    [InlineData("host=h;username=u", "password")]
    public void Missing_required_key_throws_a_readable_error(string secret, string missingKey)
    {
        var act = () => RabbitConnectionSettings.From(secret);

        act.Should().Throw<InvalidOperationException>().WithMessage($"Connection is missing '{missingKey}'.");
    }

    [Theory]
    [InlineData("host=h;username=u;password=p;amqpPort=abc", "amqpPort")]
    [InlineData("host=h;username=u;password=p;amqpPort=70000", "amqpPort")]
    [InlineData("host=h;username=u;password=p;managementUrl=not-a-url", "managementUrl")]
    [InlineData("host=h;username=u;password=p;managementUrl=ftp%3A%2F%2Fh", "managementUrl")]
    [InlineData("host=h;username=u;password=p;tls=maybe", "tls")]
    public void Invalid_values_throw_a_readable_error_naming_the_key(string secret, string key)
    {
        var act = () => RabbitConnectionSettings.From(secret);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{key}'*");
    }

    [Fact]
    public void ToString_never_contains_the_password()
    {
        var s = RabbitConnectionSettings.From("host=h;username=u;password=hunter2");

        s.ToString().Should().NotContain("hunter2");
    }
}
