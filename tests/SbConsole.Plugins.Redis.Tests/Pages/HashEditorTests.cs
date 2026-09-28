using System.Globalization;
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

    private void SetupFields(HashFieldPage page) =>
        Operations.ScanHashFieldsAsync(Secret, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(page);

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderWithAsync(params HashFieldEntry[] fields)
    {
        SetupFields(new HashFieldPage(fields, 0, true, fields.Length));
        var cut = RenderEditor();
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Masks_sensitive_fields_and_reveals_them_on_click()
    {
        var cut = await RenderWithAsync(new HashFieldEntry("csrf_token", "abc123", 6), new HashFieldEntry("username", "nima", 4));

        var values = cut.FindAll(".hash-field-value");
        values.Any(v => v.TextContent.Contains("abc123")).Should().BeFalse();
        values.Any(v => v.TextContent.Contains("••••••••")).Should().BeTrue();
        values.Any(v => v.TextContent.Contains("nima")).Should().BeTrue();

        cut.Find(".hash-field-reveal").Click();
        await SettleAsync(cut);

        await Audit.Received(1).RecordAsync("redis.field.reveal", $"{ConnectionName}/db0/{Key}.csrf_token", ActionRisk.Safe, true, null, Arg.Any<CancellationToken>());
        cut.FindAll(".hash-field-value").Any(e => e.TextContent.Contains("abc123")).Should().BeTrue();
    }

    [Fact]
    public async Task Adding_a_field_calls_the_set_operation_and_reloads()
    {
        var cut = await RenderWithAsync();

        cut.FindAll("#hash-editor-new-field").Should().BeEmpty();
        cut.Find(".hash-editor-add-toggle").Click();
        cut.Find("#hash-editor-new-field").Input("plan");
        cut.Find("#hash-editor-new-value").Input("pro");
        cut.Find(".hash-editor-add-field").Click();
        await SettleAsync(cut);

        await Operations.Received(1).SetHashFieldAsync(Secret, 0, Key, "plan", "pro", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_a_field_from_the_row_menu_calls_the_delete_operation()
    {
        var cut = await RenderWithAsync(new HashFieldEntry("plan", "pro", 3));

        cut.Find(".hash-field-menu button").Click();
        cut.Find(".hash-field-delete").Click();
        await SettleAsync(cut);

        await Operations.Received(1).DeleteHashFieldAsync(Secret, 0, Key, "plan", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Editing_a_field_from_the_row_menu_prefills_the_form()
    {
        var cut = await RenderWithAsync(new HashFieldEntry("plan", "pro", 3));

        cut.Find(".hash-field-menu button").Click();
        cut.Find(".hash-field-edit").Click();

        cut.Find("#hash-editor-new-field").GetAttribute("value").Should().Be("plan");
        cut.Find("#hash-editor-new-value").GetAttribute("value").Should().Be("pro");
    }

    [Fact]
    public async Task Filtering_rescans_with_an_hscan_match_pattern()
    {
        var cut = await RenderWithAsync(new HashFieldEntry("plan", "pro", 3));

        cut.Find("#hash-editor-filter").Input("rec");
        cut.Find("#hash-editor-filter").KeyDown("Enter");
        await SettleAsync(cut);

        await Operations.Received().ScanHashFieldsAsync(Secret, Arg.Is(0), Key, "*rec*", Arg.Is(0L), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Json_view_shows_the_fields_as_one_object_with_secrets_still_masked()
    {
        var cut = await RenderWithAsync(new HashFieldEntry("userId", "77120", 5), new HashFieldEntry("csrf", "s3cr3t", 6));

        cut.Find(".hash-editor-view-json").Click();

        var json = cut.Find(".hash-editor-json").TextContent;
        json.Should().Contain("\"userId\": \"77120\"");
        json.Should().NotContain("s3cr3t");
        cut.FindAll(".hash-field-row").Should().BeEmpty();
    }

    [Fact]
    public async Task An_iso_date_older_than_thirty_days_gets_an_age_hint()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-57).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var recent = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var cut = await RenderWithAsync(new HashFieldEntry("createdAt", old, 20), new HashFieldEntry("lastSeen", recent, 20));

        var ages = cut.FindAll(".hash-field-age");
        ages.Should().ContainSingle().Which.TextContent.Should().Be("57 days old");
    }

    [Fact]
    public async Task Sizes_above_one_kilobyte_are_warned_and_long_json_arrays_are_summarised()
    {
        var items = Enumerable.Range(0, 1206).Select(i => $"\"sku_{i:0000}\"");
        var array = "[" + string.Join(",", items) + "]";
        var cut = await RenderWithAsync(new HashFieldEntry("recentViews", array, 34 * 1024), new HashFieldEntry("mfa", "true", 4));

        var sizes = cut.FindAll(".hash-field-size");
        sizes[0].ClassList.Should().Contain("rds-warn");
        sizes[0].TextContent.Should().Be("34 KB");
        sizes[1].ClassList.Should().NotContain("rds-warn");
        var preview = cut.FindAll(".hash-field-value")[0].TextContent;
        preview.Should().StartWith("[ \"sku_0000\"").And.EndWith("more ]").And.Contain("1,204 more");
    }

    [Fact]
    public async Task Footer_shows_shown_of_total_and_load_more_when_incomplete()
    {
        SetupFields(new HashFieldPage([new HashFieldEntry("a", "1", 1), new HashFieldEntry("b", "2", 1)], 7, false, 42));
        var cut = RenderEditor();
        await SettleAsync(cut);

        cut.Find(".hash-editor-field-count").TextContent.Should().Contain("2 of 42 fields");
        cut.Find(".hash-editor-load-more").Click();
        await SettleAsync(cut);

        await Operations.Received(1).ScanHashFieldsAsync(Secret, Arg.Is(0), Key, null, Arg.Is(7L), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
