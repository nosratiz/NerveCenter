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

    private void SetupValue(StringValue value) =>
        Operations.GetStringAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(value);

    [Fact]
    public async Task Renders_the_summary_line_and_pretty_prints_json_by_default()
    {
        SetupValue(new StringValue(Key, TimeSpan.FromMinutes(5), 1229, "{\"currency\":\"GBP\",\"vatRate\":0.2}", true));
        var cut = RenderEditor();
        await SettleAsync(cut);

        var summary = cut.Find(".string-editor-summary").TextContent;
        summary.Should().Contain("1 KB").And.Contain("TTL 5m");
        cut.Find(".string-editor-json-badge").TextContent.Should().Contain("detected JSON");
        cut.Find(".string-editor-view-json").ClassList.Should().Contain("rds-active");
        cut.Find(".string-editor-code").TextContent.Should().Contain("\"currency\": \"GBP\"").And.Contain("\n");
    }

    [Fact]
    public async Task A_key_without_ttl_says_ttl_none_and_plain_text_defaults_to_the_text_view()
    {
        SetupValue(new StringValue(Key, null, 5, "hello", false));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".string-editor-summary").TextContent.Should().Contain("TTL none");
        cut.FindAll(".string-editor-json-badge").Should().BeEmpty();
        cut.Find(".string-editor-view-text").ClassList.Should().Contain("rds-active");
        cut.Find(".string-editor-code").TextContent.Should().Be("hello");
    }

    [Fact]
    public async Task The_hex_view_shows_the_utf8_bytes()
    {
        SetupValue(new StringValue(Key, null, 2, "hi", false));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".string-editor-view-hex").Click();

        cut.Find(".string-editor-code").TextContent.Should().Contain("68 69");
    }

    [Fact]
    public async Task Copy_writes_the_raw_value_to_the_clipboard()
    {
        SetupValue(new StringValue(Key, null, 5, "hello", false));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".string-editor-copy").Click();

        JSInterop.VerifyInvoke("navigator.clipboard.writeText").Arguments.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public async Task Edit_opens_the_value_in_a_text_field()
    {
        SetupValue(new StringValue(Key, TimeSpan.FromMinutes(5), 13, "{\"a\":1}", true));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.FindAll("#string-editor-value").Should().BeEmpty();
        cut.Find(".string-editor-edit").Click();

        cut.Find("#string-editor-value").GetAttribute("value").Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task Save_is_disabled_until_the_value_changes()
    {
        SetupValue(new StringValue(Key, null, 5, "hello", false));
        var cut = RenderEditor();
        await SettleAsync(cut);
        cut.Find(".string-editor-edit").Click();

        cut.Find("button.string-editor-save").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#string-editor-value").Input("hello world");

        cut.Find("button.string-editor-save").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Saving_preserves_the_keys_current_ttl_and_calls_the_set_operation()
    {
        var ttl = TimeSpan.FromMinutes(5);
        SetupValue(new StringValue(Key, ttl, 5, "hello", false));
        var cut = RenderEditor();
        await SettleAsync(cut);
        cut.Find(".string-editor-edit").Click();

        cut.Find("#string-editor-value").Input("updated");
        cut.Find("button.string-editor-save").Click();
        await SettleAsync(cut);

        await Operations.Received(1).SetStringAsync(Secret, 0, Key, "updated", ttl, Arg.Any<CancellationToken>());
    }
}
