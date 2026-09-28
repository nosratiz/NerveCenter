using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Components;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class StringEditorTests : RedisPageTestBase
{
    private const string Key = "session:2:data";

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderEditor()
    {
        return RenderWithPopovers(builder =>
        {
            builder.OpenComponent<StringEditor>(0);
            builder.AddComponentParameter(1, nameof(StringEditor.ConnectionId), ConnectionId);
            builder.AddComponentParameter(2, nameof(StringEditor.ConnectionName), ConnectionName);
            builder.AddComponentParameter(3, nameof(StringEditor.Database), 0);
            builder.AddComponentParameter(4, nameof(StringEditor.Key), Key);
            builder.CloseComponent();
        });
    }

    [Fact]
    public async Task Renders_the_current_value_and_a_JSON_badge_when_applicable()
    {
        Operations.GetStringAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StringValue(Key, TimeSpan.FromMinutes(5), 13, "{\"a\":1}", true));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find("#string-editor-value").GetAttribute("value").Should().Be("{\"a\":1}");
        cut.Find(".string-editor-json-badge").TextContent.Should().Contain("JSON");
    }

    [Fact]
    public async Task Save_is_disabled_until_the_value_changes()
    {
        Operations.GetStringAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StringValue(Key, null, 5, "hello", false));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find("button.string-editor-save").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#string-editor-value").Input("hello world");

        cut.Find("button.string-editor-save").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Saving_preserves_the_keys_current_ttl_and_calls_the_set_operation()
    {
        var ttl = TimeSpan.FromMinutes(5);
        Operations.GetStringAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StringValue(Key, ttl, 5, "hello", false));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find("#string-editor-value").Input("updated");
        cut.Find("button.string-editor-save").Click();
        await SettleAsync(cut);

        await Operations.Received(1).SetStringAsync(Secret, 0, Key, "updated", ttl, Arg.Any<CancellationToken>());
    }
}
