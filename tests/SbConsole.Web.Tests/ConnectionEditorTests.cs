using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Connections;

namespace SbConsole.Web.Tests;

public class ConnectionEditorTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    private sealed class FakePlugin(string kind, string displayName, Type? formType = null) : IPlugin
    {
        public string Id => kind;
        public string DisplayName => displayName;
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => displayName;
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Type? ConnectionFormComponentType => formType;
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true, Identity: "111122223333", Checks: [new ConnectionCheck("Widgets visible", ConnectionCheckStatus.Passed, "3")]));
    }

    // A trivial ConnectionFormComponentType, so tests can assert the DynamicComponent path is
    // taken without depending on the real AwsConnectionFields (a different project/test suite).
    private sealed class FakeFormComponent : Microsoft.AspNetCore.Components.ComponentBase
    {
        [Microsoft.AspNetCore.Components.Parameter] public string? InitialSecret { get; set; }
        [Microsoft.AspNetCore.Components.Parameter] public Microsoft.AspNetCore.Components.EventCallback<string> SecretChanged { get; set; }
        [Microsoft.AspNetCore.Components.Parameter] public bool IsProd { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "input");
            builder.AddAttribute(1, "id", "fake-form-field");
            builder.AddAttribute(2, "value", InitialSecret);
            builder.AddAttribute(3, "onchange", Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Microsoft.AspNetCore.Components.ChangeEventArgs>(
                this, e => SecretChanged.InvokeAsync((string?)e.Value ?? "")));
            builder.CloseElement();
        }
    }

    private readonly TestDb _testDb = new();

    // MudSelect's dropdown content is rendered by <MudPopoverProvider/>, a separate component that
    // the real app hosts once in its layout and which portals every open popover's markup into
    // itself. Plain Render<ConnectionEditor>(...) doesn't surface it (confirmed: cut.Find("div.mud-
    // list-item") found nothing), so -- unlike the old AddEditConnectionDialogTests -- we don't need
    // any IMudDialogInstance/CascadingValue scaffolding (ConnectionEditor isn't a MudDialog), just a
    // sibling MudPopoverProvider alongside the component under test.
    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderEditor(
        ConnectionInfo? existing = null, EventCallback<bool>? onClosed = null)
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<ConnectionEditor>(1);
            if (existing is not null)
            {
                builder.AddComponentParameter(2, nameof(ConnectionEditor.Existing), existing);
            }
            if (onClosed is not null)
            {
                builder.AddComponentParameter(3, nameof(ConnectionEditor.OnClosed), onClosed.Value);
            }

            builder.CloseComponent();
        };

        return Render(fragment);
    }

    public ConnectionEditorTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin("azure-servicebus", "Azure Service Bus")]);
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SbConsole.Core.Data.SbcDbContext>>(_testDb);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(new byte[32]));
        Services.AddSingleton<IAuditWriter>(Substitute.For<IAuditWriter>());
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<CreateConnectionCommandHandler>();
        Services.AddSingleton<UpdateConnectionCommandHandler>();
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
    }

    [Fact]
    public void Add_mode_requires_name_kind_and_secret_before_save_enables()
    {
        var cut = RenderEditor();

        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input#connection-name").Input("sb-dev");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://x");

        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Edit_mode_prefills_name_and_does_not_require_a_new_secret()
    {
        var existing = new ConnectionInfo(Guid.NewGuid(), "sb-dev", "azure-servicebus", ["dev"]);

        var cut = RenderEditor(existing: existing);

        cut.Find("input#connection-name").GetAttribute("value").Should().Be("sb-dev");
        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Save_in_add_mode_invokes_OnClosed_with_true()
    {
        var closedWith = new List<bool>();
        var cut = RenderEditor(onClosed: EventCallback.Factory.Create<bool>(this, v => closedWith.Add(v)));

        cut.Find("input#connection-name").Input("sb-dev");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://x");
        cut.Find("button.save-connection").Click();
        await Task.Delay(50);

        closedWith.Should().Equal(true);
    }

    [Fact]
    public void Cancel_invokes_OnClosed_with_false()
    {
        var closedWith = new List<bool>();
        var cut = RenderEditor(onClosed: EventCallback.Factory.Create<bool>(this, v => closedWith.Add(v)));

        cut.Find("button.cancel-connection").Click();

        closedWith.Should().Equal(false);
    }

    [Fact]
    public void A_plugin_with_a_custom_form_component_hosts_it_instead_of_the_flat_textbox()
    {
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin("aws", "AWS SQS/SNS", typeof(FakeFormComponent))]);

        var cut = RenderEditor();
        cut.Find("input#connection-name").Input("aws-dev");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();

        cut.FindAll("#fake-form-field").Should().ContainSingle();
        cut.FindAll("input#connection-secret").Should().BeEmpty();
    }

    [Fact]
    public async Task Testing_an_unsaved_connection_calls_the_plugin_directly_and_shows_the_result()
    {
        var cut = RenderEditor();
        cut.Find("input#connection-name").Input("sb-dev");
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://x");

        cut.Find("button.test-connection").Click();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().Contain("111122223333");
        cut.Markup.Should().Contain("Widgets visible");
    }

    [Fact]
    public async Task Testing_an_existing_connection_routes_through_the_persisted_handler()
    {
        var created = await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", "Endpoint=sb://x", ["dev"], "admin"));
        var existing = new ConnectionInfo(created.Value, "sb-dev", "azure-servicebus", ["dev"]);

        var cut = RenderEditor(existing: existing);
        cut.Find("button.test-connection").Click();
        await Task.Delay(50);
        cut.Render();

        cut.Markup.Should().Contain("111122223333");
        await using var db = _testDb.CreateDbContext();
        var saved = await db.Connections.SingleAsync(c => c.Id == existing.Id);
        saved.LastTestSucceeded.Should().BeTrue();
    }
}
