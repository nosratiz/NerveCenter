using Bunit;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Components;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class HashEditorTests : RedisPageTestBase
{
    private const string Key = "session:1";

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderEditor()
    {
        var cut = RenderWithPopovers(builder =>
        {
            builder.OpenComponent<HashEditor>(0);
            builder.AddComponentParameter(1, nameof(HashEditor.ConnectionId), ConnectionId);
            builder.AddComponentParameter(2, nameof(HashEditor.ConnectionName), ConnectionName);
            builder.AddComponentParameter(3, nameof(HashEditor.Database), 0);
            builder.AddComponentParameter(4, nameof(HashEditor.Key), Key);
            builder.CloseComponent();
        });
        return cut;
    }

    [Fact]
    public async Task Masks_sensitive_fields_and_reveals_them_on_click()
    {
        Operations.ScanHashFieldsAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new HashFieldPage([new HashFieldEntry("csrf_token", "abc123", 6), new HashFieldEntry("username", "nima", 4)], 0, true, 2));
        var cut = RenderEditor();
        await SettleAsync(cut);

        var values = cut.FindAll(".hash-field-value");
        values.Any(v => v.TextContent.Contains("abc123")).Should().BeFalse();
        values.Any(v => v.TextContent.Contains("nima")).Should().BeTrue();

        cut.Find(".hash-field-reveal").Click();
        await SettleAsync(cut);

        await Audit.Received(1).RecordAsync("redis.field.reveal", $"{ConnectionName}/db0/{Key}.csrf_token", ActionRisk.Safe, true, null, Arg.Any<CancellationToken>());
        cut.FindAll(".hash-field-value").Any(e => e.TextContent.Contains("abc123")).Should().BeTrue();
    }

    [Fact]
    public async Task Adding_a_field_calls_the_set_operation_and_reloads()
    {
        Operations.ScanHashFieldsAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new HashFieldPage([], 0, true, 0));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find("#hash-editor-new-field").Input("plan");
        cut.Find("#hash-editor-new-value").Input("pro");
        cut.Find(".hash-editor-add-field").Click();
        await SettleAsync(cut);

        await Operations.Received(1).SetHashFieldAsync(Secret, 0, Key, "plan", "pro", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_a_field_calls_the_delete_operation()
    {
        Operations.ScanHashFieldsAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new HashFieldPage([new HashFieldEntry("plan", "pro", 3)], 0, true, 1));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".hash-field-delete").Click();
        await SettleAsync(cut);

        await Operations.Received(1).DeleteHashFieldAsync(Secret, 0, Key, "plan", Arg.Any<CancellationToken>());
    }
}
