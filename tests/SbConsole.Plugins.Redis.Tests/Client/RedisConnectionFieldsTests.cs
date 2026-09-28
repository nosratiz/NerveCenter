using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisConnectionFieldsTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public RedisConnectionFieldsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<RedisConnectionFields> RenderFields(string? initialSecret, out List<string> emitted, bool isProd = false)
    {
        var captured = new List<string>();
        emitted = captured;
        return Render<RedisConnectionFields>(parameters => parameters
            .Add(p => p.InitialSecret, initialSecret)
            .Add(p => p.IsProd, isProd)
            .Add(p => p.SecretChanged, EventCallback.Factory.Create<string>(this, s => captured.Add(s))));
    }

    [Fact]
    public void Parses_the_initial_secret_into_the_host_field()
    {
        var initial = new RedisConnectionSettings(
            RedisTopology.Standalone, ["redis-01:6380"], "sbconsole", "hunter2", true, null, false, 0)
            .ToSecretString();

        var cut = RenderFields(initial, out _);

        cut.Find("input#redis-conn-host").GetAttribute("value").Should().Be("redis-01:6380");
    }

    [Fact]
    public void Editing_the_host_raises_secret_changed_with_the_updated_secret()
    {
        var initial = new RedisConnectionSettings(
            RedisTopology.Standalone, ["redis-01:6380"], "sbconsole", "hunter2", true, null, false, 0)
            .ToSecretString();

        var cut = RenderFields(initial, out var emitted);

        cut.Find("input#redis-conn-host").Input("redis-02:6380");

        emitted.Should().NotBeEmpty();
        RedisConnectionSettings.Parse(emitted.Last()).EndpointHostPorts.Should().Equal("redis-02:6380");
    }

    [Fact]
    public void Warns_when_prod_tagged_without_tls()
    {
        var initial = new RedisConnectionSettings(
            RedisTopology.Standalone, ["redis-01:6380"], "sbconsole", "hunter2", Ssl: false, null, false, 0)
            .ToSecretString();

        var cut = RenderFields(initial, out _, isProd: true);

        cut.Markup.Should().Contain("TLS");
    }

    [Fact]
    public void Typing_an_incomplete_host_port_does_not_crash_the_component()
    {
        // Immediate="true" means every keystroke round-trips through RaiseChangedAsync ->
        // ToSecretString -> ConfigurationOptions.EndPoints.Add. Typing "host:6379" passes through
        // the intermediate state "localhost:", which EndPoints.Add cannot parse and throws
        // ArgumentException for -- this must be swallowed, not propagate into the Blazor circuit.
        var initial = new RedisConnectionSettings(
            RedisTopology.Standalone, ["localhost:6379"], null, null, false, null, false, 0)
            .ToSecretString();

        var cut = RenderFields(initial, out _);

        var act = () => cut.Find("input#redis-conn-host").Input("localhost:");

        act.Should().NotThrow();
    }

    [Fact]
    public void Completing_the_host_after_an_unparsable_intermediate_state_still_raises_secret_changed()
    {
        var initial = new RedisConnectionSettings(
            RedisTopology.Standalone, ["localhost:6379"], null, null, false, null, false, 0)
            .ToSecretString();

        var cut = RenderFields(initial, out var emitted);

        cut.Find("input#redis-conn-host").Input("localhost:");
        cut.Find("input#redis-conn-host").Input("localhost:6379");

        emitted.Should().NotBeEmpty();
        RedisConnectionSettings.Parse(emitted.Last()).EndpointHostPorts.Should().Equal("localhost:6379");
    }

    [Fact]
    public void Host_containing_a_comma_does_not_raise_secret_changed()
    {
        // A comma inside the host would silently corrupt the '|'-delimited secret envelope --
        // ConfigurationOptions treats comma as its own field separator with no escape syntax.
        var initial = new RedisConnectionSettings(
            RedisTopology.Standalone, ["localhost:6379"], null, null, false, null, false, 0)
            .ToSecretString();

        var cut = RenderFields(initial, out var emitted);
        var countBefore = emitted.Count;

        var act = () => cut.Find("input#redis-conn-host").Input("host,x:6379");

        act.Should().NotThrow();
        emitted.Count.Should().Be(countBefore);
    }

    [Fact]
    public void Service_name_containing_a_comma_does_not_raise_secret_changed()
    {
        // Same comma-as-field-separator hazard as the host field, but for the Sentinel-only
        // service name field.
        var initial = new RedisConnectionSettings(
            RedisTopology.Sentinel, ["sentinel-1:26379"], null, null, false, "uk-prod", false, 0)
            .ToSecretString();

        var cut = RenderFields(initial, out var emitted);
        var countBefore = emitted.Count;

        var act = () => cut.Find("input#redis-conn-servicename").Input("uk,prod");

        act.Should().NotThrow();
        emitted.Count.Should().Be(countBefore);
    }

    [Fact]
    public void A_well_formed_but_invalid_initial_secret_renders_with_defaults_instead_of_throwing()
    {
        // "bogus|0|host:6379" has three '|'-delimited parts (so it does NOT hit the FormatException
        // branch) but "bogus" isn't a RedisTopology member -- Enum.Parse throws ArgumentException,
        // which OnInitialized's catch (ArgumentException) must also swallow.
        var act = () => RenderFields("bogus|0|host:6379", out _);

        act.Should().NotThrow();
    }
}
