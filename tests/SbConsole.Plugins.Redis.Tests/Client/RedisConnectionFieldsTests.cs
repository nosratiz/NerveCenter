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
}
