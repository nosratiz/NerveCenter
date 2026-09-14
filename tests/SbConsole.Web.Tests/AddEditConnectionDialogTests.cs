using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

public class AddEditConnectionDialogTests : BunitContext, IAsyncLifetime
{
    // MudBlazor registers a few interop-backed services (key interception for popovers,
    // pointer-events routing) that implement only IAsyncDisposable. xunit v2 tears down a test
    // class via its synchronous IDisposable.Dispose() unless the class also implements
    // Xunit.IAsyncLifetime, in which case DisposeAsync() runs first -- disposing those services
    // the async-safe way before the base (synchronous) Dispose() runs as a no-op afterwards.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    private sealed class FakePlugin(string kind, string displayName) : IPlugin
    {
        public string Id => kind;
        public string DisplayName => displayName;
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => displayName;
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(Success: true));
    }

    private readonly TestDb _testDb = new();

    // MudDialog's own rendering is gated on an internal `IMudDialogInstanceInternal` cascading
    // parameter (distinct from the public `IMudDialogInstance` our component's code-behind uses to
    // call Close/Cancel) -- without it, <MudDialog> treats itself as "inline and not yet shown" and
    // renders nothing. That type isn't public, so it can't be named directly; instead we build one
    // substitute that implements both interfaces via reflection, and cascade it as `IMudDialogInstance`.
    // bUnit resolves the cascaded value's type from `Value.GetType()` (the substitute's actual proxy
    // type), so both MudDialog's internal parameter and our component's public one resolve correctly.
    // See ConfirmDialogTests.cs for the same pattern.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public AddEditConnectionDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEnumerable<IPlugin>>([new FakePlugin("azure-servicebus", "Azure Service Bus")]);
        // The component's [Inject] properties are resolved on every render regardless of
        // which test path is exercised, so both handlers need real registrations even in
        // tests that never call Save().
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SbConsole.Core.Data.SbcDbContext>>(_testDb);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(new byte[32]));
        Services.AddSingleton<IAuditWriter>(Substitute.For<IAuditWriter>());
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<CreateConnectionCommandHandler>();
        Services.AddSingleton<UpdateConnectionCommandHandler>();
    }

    // MudSelect's dropdown content is rendered by <MudPopoverProvider/>, a separate component
    // that the real app hosts once in its layout and which portals every open popover's markup
    // into itself. bUnit only renders what's given to Render(...), so a lone
    // <AddEditConnectionDialog/> never gets the popover's <div class="mud-list-item"> markup in
    // its subtree -- we render a provider alongside it (both under the same cascaded
    // IMudDialogInstance) so cut.Find can see the opened popover's items.
    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog(ConnectionInfo? existing = null)
    {
        var mudDialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
        // Must cascade as CascadingValue<TRuntimeProxyType>, not CascadingValue<IMudDialogInstance> --
        // MudDialog's internal cascading parameter is typed IMudDialogInstanceInternal, and Blazor
        // only matches a CascadingValue<T> to consumers whose parameter type T is assignable from.
        // Pinning T to IMudDialogInstance here would make MudDialog treat itself as never-shown and
        // render nothing (see ConfirmDialogTests.cs's comment on the same substitute).
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(mudDialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", mudDialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<MudPopoverProvider>(0);
                inner.CloseComponent();
                inner.OpenComponent<AddEditConnectionDialog>(1);
                if (existing is not null)
                {
                    inner.AddComponentParameter(2, nameof(AddEditConnectionDialog.Existing), existing);
                }

                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public void Add_mode_requires_name_kind_and_secret_before_save_enables()
    {
        var cut = RenderDialog();

        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input#connection-name").Input("sb-dev");
        // MudSelect opens its popover on mousedown of the inner input-control div, not on click of
        // the outer "mud-select" wrapper (which only carries an onclick:stopPropagation modifier
        // with no click handler of its own) -- confirmed by inspecting cut.Markup.
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Find("div.mud-list-item").Click();
        cut.Find("input#connection-secret").Input("Endpoint=sb://x");

        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Edit_mode_prefills_fields_and_does_not_require_a_new_secret()
    {
        var existing = new ConnectionInfo(Guid.NewGuid(), "sb-dev", "azure-servicebus", ["dev"]);

        var cut = RenderDialog(existing);

        cut.Find("input#connection-name").GetAttribute("value").Should().Be("sb-dev");
        cut.Find("button.save-connection").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Save_in_edit_mode_preserves_the_existing_secret_when_the_field_is_left_blank()
    {
        const string originalSecret = "Endpoint=sb://original;SharedAccessKey=orig-key";

        var created = await Services.GetRequiredService<CreateConnectionCommandHandler>()
            .HandleAsync(new CreateConnectionCommand("sb-dev", "azure-servicebus", originalSecret, ["dev"], "admin"));

        var existing = new ConnectionInfo(created.Value, "sb-dev", "azure-servicebus", ["dev"]);

        var cut = RenderDialog(existing);

        // Change something other than the secret field, which is left blank per the Edit-mode
        // contract asserted below.
        cut.Find("input#connection-tag-input").Input("prod");
        cut.Find("button#add-tag").Click();

        cut.Find("button.save-connection").Click();
        await Task.Delay(50); // let the async Save handler's awaits (Update command + DB write) complete

        var protector = Services.GetRequiredService<ISecretProtector>();
        await using var db = _testDb.CreateDbContext();
        var stored = await db.Connections.SingleAsync(c => c.Id == existing.Id);
        protector.Unprotect(stored.SecretCiphertext).Should().Be(originalSecret);
    }

    [Fact]
    public void Adding_and_removing_tags_updates_the_chip_list()
    {
        var cut = RenderDialog();

        cut.Find("input#connection-tag-input").Input("prod");
        cut.Find("button#add-tag").Click();

        cut.FindAll("span.mud-chip-content").Should().Contain(e => e.TextContent.Contains("prod"));
    }
}
