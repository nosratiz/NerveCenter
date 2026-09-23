using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class ConnectionsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    // The brief's original DI setup registered Array.Empty<IPlugin>(), which leaves the Kind
    // MudSelect with zero MudSelectItem children -- fine for every test that seeds connections
    // straight through CreateConnectionCommandHandler, but Saving_from_the_panel drives the Add
    // panel's UI and needs a real option to click. Mirrors ConnectionEditorTests.FakePlugin.
    private sealed class FakePlugin : IPlugin
    {
        public string Id => "azure-servicebus";
        public string DisplayName => "Azure Service Bus";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => "azure-servicebus";
        public string ConnectionKindDisplayName => "Azure Service Bus";
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));
    }

    // A plugin with a stateful custom form, so Editing_a_different_row_gives_the_custom_form_a_
    // fresh_instance below can prove the @key-forced remount actually happens -- a component that
    // merely re-hydrates the same value each render wouldn't distinguish "same instance,
    // parameters updated" from "brand-new instance".
    private sealed class FakeFormPlugin : IPlugin
    {
        public string Id => "aws";
        public string DisplayName => "AWS SQS/SNS";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => "aws";
        public string ConnectionKindDisplayName => "AWS SQS/SNS";
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Type? ConnectionFormComponentType => typeof(StatefulFakeFormComponent);
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));
    }

    private sealed class StatefulFakeFormComponent : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.Parameter] public string? InitialSecret { get; set; }
        [Microsoft.AspNetCore.Components.Parameter] public Microsoft.AspNetCore.Components.EventCallback<string> SecretChanged { get; set; }
        [Microsoft.AspNetCore.Components.Parameter] public bool IsProd { get; set; }

        // Deliberately keeps its own field-level state past OnInitialized (unlike the read-once
        // hydration DynamicComponent forms document) so a stale reused instance would visibly leak
        // a previous row's typed-but-unsaved value into the next row's panel.
        private string _value = "";

        protected override void OnInitialized() => _value = InitialSecret ?? "";

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "input");
            builder.AddAttribute(1, "id", "fake-form-field");
            builder.AddAttribute(2, "value", _value);
            builder.AddAttribute(3, "onchange", Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Microsoft.AspNetCore.Components.ChangeEventArgs>(
                this, e =>
                {
                    _value = (string?)e.Value ?? "";
                    return SecretChanged.InvokeAsync(_value);
                }));
            builder.CloseElement();
        }
    }

    private readonly TestDb _testDb = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private static readonly byte[] Key = new byte[32];

    public ConnectionsPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin()]);
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SbConsole.Core.Data.SbcDbContext>>(_testDb);
        Services.AddSingleton(_audit);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(Key));
        Services.AddSingleton(new FakeTimeProvider());
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
        Services.AddSingleton<CreateConnectionCommandHandler>();
        Services.AddSingleton<DeleteConnectionCommandHandler>();
        Services.AddSingleton<UpdateConnectionCommandHandler>();
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
        Services.AddSingleton(Substitute.For<SbConsole.Sdk.IConfirmationService>());
    }

    // MudSelect's dropdown content is rendered by <MudPopoverProvider/>, a separate component that
    // the real app hosts once in its layout and which portals every open popover's markup into
    // itself. Plain Render<Connections>() doesn't surface it (confirmed: cut.Find("div.mud-list-
    // item") found nothing once the panel opens a MudSelect), so render a sibling
    // MudPopoverProvider alongside the page under test -- same pattern as
    // ConnectionEditorTests.RenderEditor / AuditPageTests.RenderPage.
    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage()
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Connections>(1);
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public async Task Lists_seeded_connections()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = RenderPage();

        cut.Markup.Should().Contain("sb-dev");
    }

    [Fact]
    public async Task Delete_calls_the_handler_only_when_confirmation_service_returns_true()
    {
        var confirmation = Substitute.For<SbConsole.Sdk.IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>()).Returns(true);
        Services.AddSingleton(confirmation);

        var created = await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = RenderPage();
        cut.Find("button.delete-connection").Click();
        await Task.Delay(50);

        await confirmation.Received(1).ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>());

        var remaining = await Services.GetRequiredService<ListConnectionsQueryHandler>().HandleAsync();
        remaining.Should().NotContain(c => c.Name == "sb-dev");
    }

    [Fact]
    public async Task Delete_leaves_the_connection_when_confirmation_service_returns_false()
    {
        var confirmation = Substitute.For<SbConsole.Sdk.IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>()).Returns(false);
        Services.AddSingleton(confirmation);

        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = RenderPage();
        cut.Find("button.delete-connection").Click();
        await Task.Delay(50);

        await confirmation.Received(1).ConfirmAsync("Delete", "sb-dev", false, null, Arg.Any<CancellationToken>());

        var remaining = await Services.GetRequiredService<ListConnectionsQueryHandler>().HandleAsync();
        remaining.Should().Contain(c => c.Name == "sb-dev");
    }

    [Fact]
    public async Task Shows_never_tested_for_a_connection_that_has_no_recorded_test()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = RenderPage();

        cut.Markup.Should().Contain("Never tested");
    }

    [Fact]
    public async Task Editor_panel_is_hidden_until_Add_or_Edit_is_clicked()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "secret", ["dev"], "admin"));

        var cut = RenderPage();

        cut.FindAll("input#connection-name").Should().BeEmpty();

        cut.Find("button.add-connection-action").Click();

        cut.FindAll("input#connection-name").Should().ContainSingle();
    }

    [Fact]
    public async Task Editing_a_different_row_swaps_the_panel_to_that_connections_data()
    {
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev-a", "azure-servicebus", "secret-a", ["dev"], "admin"));
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev-b", "azure-servicebus", "secret-b", ["dev"], "admin"));

        var cut = RenderPage();
        cut.FindAll("button.edit-connection")[0].Click();
        cut.Find("input#connection-name").GetAttribute("value").Should().Be("sb-dev-a");

        cut.FindAll("button.edit-connection")[1].Click();
        cut.Find("input#connection-name").GetAttribute("value").Should().Be("sb-dev-b");
    }

    [Fact]
    public async Task Saving_from_the_panel_refreshes_the_list_and_closes_the_panel()
    {
        var cut = RenderPage();
        cut.Find("button.add-connection-action").Click();

        cut.Find("input#connection-name").Input("sb-new");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://new");
        cut.Find("button.save-connection").Click();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().Contain("sb-new");
        cut.FindAll("input#connection-name").Should().BeEmpty();
    }

    [Fact]
    public async Task Editing_a_different_row_gives_the_custom_form_a_fresh_instance_not_the_previous_rows_typed_state()
    {
        // Replaces the constructor's azure-servicebus FakePlugin -- this test only needs "aws"
        // rows, driven through a custom form component instead of the flat secret textbox.
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakeFormPlugin()]);

        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("aws-a", "aws", "secret-a", [], "admin"));
        await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("aws-b", "aws", "secret-b", [], "admin"));

        var cut = RenderPage();
        cut.FindAll("button.edit-connection")[0].Click();
        cut.Find("#fake-form-field").Change("typed-but-unsaved");
        cut.Find("#fake-form-field").GetAttribute("value").Should().Be("typed-but-unsaved");

        // Switching to row B's Edit uses a different @key (the connection's own Id), which per
        // Connections.razor's documented @key contract forces a brand-new ConnectionEditor (and
        // therefore a brand-new DynamicComponent) instance. Saved secrets are write-only and never
        // round-tripped to the browser, so a fresh instance's InitialSecret is always "" -- the
        // meaningful assertion is that row A's typed-but-unsaved value does NOT leak into row B's
        // panel, which is exactly what a stale/reused instance would do.
        cut.FindAll("button.edit-connection")[1].Click();
        cut.Find("#fake-form-field").GetAttribute("value").Should().Be("");
    }

    [Fact]
    public async Task Renders_a_connections_summary_as_chips()
    {
        var created = await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("aws-dev", "aws", "region=eu-west-1", [], "admin"));
        await using (var db = _testDb.CreateDbContext())
        {
            var row = await db.Connections.SingleAsync(c => c.Id == created.Value);
            row.SummaryJson = """{"Region":"eu-west-1"}""";
            await db.SaveChangesAsync();
        }

        var cut = RenderPage();

        cut.Markup.Should().Contain("eu-west-1");
    }
}
