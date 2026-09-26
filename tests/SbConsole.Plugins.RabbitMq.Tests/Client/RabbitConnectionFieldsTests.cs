using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

public class RabbitConnectionFieldsTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public RabbitConnectionFieldsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<RabbitConnectionFields> RenderFields(string? initialSecret, out List<string> emitted, bool isProd = false)
    {
        var captured = new List<string>();
        emitted = captured;
        return Render<RabbitConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, initialSecret)
            .Add(p => p.IsProd, isProd)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, s => captured.Add(s))));
    }

    private static string Value(IRenderedComponent<RabbitConnectionFields> cut, string id) =>
        cut.Find($"input#{id}").GetAttribute("value") ?? "";

    private static string Placeholder(IRenderedComponent<RabbitConnectionFields> cut, string id) =>
        cut.Find($"input#{id}").GetAttribute("placeholder") ?? "";

    private static void SetSwitch(IRenderedComponent<RabbitConnectionFields> cut, string testId, bool value) =>
        cut.Find($"input[data-testid='{testId}']").Change(value);

    [Fact]
    public void Hydrates_every_field_from_the_initial_secret()
    {
        var secret = RabbitConfigParser.Serialize(new Dictionary<string, string>
        {
            ["host"] = "rabbit.internal",
            ["amqpPort"] = "5673",
            ["managementUrl"] = "https://mgmt.internal/rabbitmq/",
            ["vhost"] = "/orders",
            ["username"] = "svc-orders",
            ["password"] = "p;a=ss",
            ["tls"] = "true",
            ["verifyCert"] = "false",
        });

        var cut = RenderFields(secret, out _);

        Value(cut, "rabbit-host").Should().Be("rabbit.internal");
        Value(cut, "rabbit-amqp-port").Should().Be("5673");
        Value(cut, "rabbit-management-url").Should().Be("https://mgmt.internal/rabbitmq/");
        Value(cut, "rabbit-vhost").Should().Be("/orders");
        Value(cut, "rabbit-username").Should().Be("svc-orders");
        Value(cut, "rabbit-password").Should().Be("p;a=ss");
        cut.Find("input#rabbit-password").GetAttribute("type").Should().Be("password");
        cut.Find("input[data-testid='rabbit-tls']").HasAttribute("checked").Should().BeTrue();
        cut.Find("input[data-testid='rabbit-verify-cert']").HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public void Typing_emits_a_secret_that_round_trips_through_the_parser()
    {
        var cut = RenderFields(null, out var emitted);

        cut.Find("input#rabbit-host").Input("localhost");
        cut.Find("input#rabbit-vhost").Input("/orders");
        cut.Find("input#rabbit-username").Input("guest");
        cut.Find("input#rabbit-password").Input("g;u=est");

        var parsed = RabbitConfigParser.Parse(emitted.Last());
        parsed.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["host"] = "localhost",
            ["vhost"] = "/orders",
            ["username"] = "guest",
            ["password"] = "g;u=est",
            ["tls"] = "false",
        });
    }

    [Fact]
    public void Turning_tls_on_writes_tls_and_verify_cert()
    {
        var cut = RenderFields("host=localhost", out var emitted);

        SetSwitch(cut, "rabbit-tls", true);

        var parsed = RabbitConfigParser.Parse(emitted.Last());
        parsed["tls"].Should().Be("true");
        parsed["verifyCert"].Should().Be("true");
    }

    [Fact]
    public void Verify_cert_switch_is_only_shown_with_tls()
    {
        var cut = RenderFields("host=localhost", out _);
        cut.FindAll("input[data-testid='rabbit-verify-cert']").Should().BeEmpty();

        SetSwitch(cut, "rabbit-tls", true);

        cut.FindAll("input[data-testid='rabbit-verify-cert']").Should().ContainSingle();
    }

    [Fact]
    public void Warns_only_for_plain_amqp_on_a_prod_connection()
    {
        const string warning = "Plain AMQP on a prod connection sends credentials unencrypted.";

        RenderFields("host=h;tls=false", out _, isProd: true).Markup.Should().Contain(warning);
        RenderFields("host=h;tls=true", out _, isProd: true).Markup.Should().NotContain(warning);
        RenderFields("host=h;tls=false", out _, isProd: false).Markup.Should().NotContain(warning);
    }

    [Fact]
    public void Placeholders_follow_host_and_tls()
    {
        var cut = RenderFields(null, out _);
        Placeholder(cut, "rabbit-vhost").Should().Be("/");
        Placeholder(cut, "rabbit-amqp-port").Should().Be("5672");

        cut.Find("input#rabbit-host").Input("rabbit.internal");
        Placeholder(cut, "rabbit-management-url").Should().Be("http://rabbit.internal:15672");

        SetSwitch(cut, "rabbit-tls", true);
        Placeholder(cut, "rabbit-management-url").Should().Be("https://rabbit.internal:15671");
        Placeholder(cut, "rabbit-amqp-port").Should().Be("5671");
    }
}
