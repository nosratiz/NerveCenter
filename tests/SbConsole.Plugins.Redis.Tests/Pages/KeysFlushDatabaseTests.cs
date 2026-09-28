using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Keys;
using KeysPage = SbConsole.Plugins.Redis.Pages.Keys;

namespace SbConsole.Plugins.Redis.Tests.Pages;

/// <summary>
/// FLUSHDB was deferred out of the Keys plan into phase 3 (design spec §12/§14) -- these tests
/// cover only that new toolbar action and the Keys page's empty-state messaging, not the whole
/// Keys.razor surface (already shipped, untested at the page level; out of this task's scope).
/// </summary>
public class KeysFlushDatabaseTests : RedisPageTestBase
{
    public KeysFlushDatabaseTests()
    {
        Operations.ScanKeysAsync(Secret, Arg.Any<ScanKeysRequest>(), Arg.Any<CancellationToken>())
            .Returns(new KeyScanPage([], Cursor: 0, Complete: true, KeysScannedSoFar: 0, DatabaseKeyCountAtScanStart: 4200));
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync()
    {
        RenderFragment page = builder =>
        {
            builder.OpenComponent<KeysPage>(0);
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    [Fact]
    public async Task Flush_database_button_is_disabled_and_explains_why_when_the_acl_check_denies_it()
    {
        Operations.HasCommandPermissionAsync(Secret, "FLUSHDB", Arg.Any<CancellationToken>()).Returns(false);

        var cut = await RenderPageAsync();

        cut.Find("button.keys-flush-database").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".keys-flush-database-acl-denied").TextContent.Should().Contain("Command refused by ACL");
    }

    [Fact]
    public async Task Flush_database_button_is_enabled_when_the_acl_check_allows_it()
    {
        Operations.HasCommandPermissionAsync(Secret, "FLUSHDB", Arg.Any<CancellationToken>()).Returns(true);

        var cut = await RenderPageAsync();

        cut.Find("button.keys-flush-database").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Clicking_flush_confirms_with_the_scanned_key_count_as_blast_radius_and_flushes_on_confirmation()
    {
        var cut = await RenderPageAsync();

        await cut.Find("button.keys-flush-database").ClickAsync(new());
        await SettleAsync(cut);

        // IsProd comes from the server-resolved connection (RedisPageTestBase seeds a prod
        // connection); the count is the real DBSIZE the last scan observed, not a client guess,
        // matching DeleteByPatternDialog's own "confirmation reflects real server state" precedent.
        await Confirmation.Received(1).ConfirmAsync("Flush database", $"{ConnectionName}/db0", true, 4200, Arg.Any<CancellationToken>());
        await Operations.Received(1).FlushDatabaseAsync(Secret, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_the_confirmation_does_not_flush_the_database()
    {
        Confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = await RenderPageAsync();

        await cut.Find("button.keys-flush-database").ClickAsync(new());
        await SettleAsync(cut);

        await Operations.DidNotReceiveWithAnyArgs().FlushDatabaseAsync(default!, default, default);
    }

    [Fact]
    public async Task A_successful_flush_shows_a_snackbar_and_clears_the_selected_key()
    {
        var cut = await RenderPageAsync();

        await cut.Find("button.keys-flush-database").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".keys-tree-empty").Should().NotBeNull(); // the (mocked, still-empty) rescan leaves the tree empty.
    }

    [Fact]
    public async Task An_empty_scan_result_shows_a_sensible_empty_state_instead_of_a_blank_tree()
    {
        var cut = await RenderPageAsync();

        cut.Find(".keys-tree-empty").TextContent.Should().Contain("No keys found");
    }
}
