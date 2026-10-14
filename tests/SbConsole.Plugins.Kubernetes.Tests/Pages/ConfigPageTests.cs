using System.Net;
using Bunit;
using FluentAssertions;
using k8s.Autorest;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Components;
using SbConsole.Sdk;
using ConfigPage = SbConsole.Plugins.Kubernetes.Pages.Config;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

public class ConfigPageTests : K8sPageTestBase
{
    private const string Changed = "It changed since you loaded it — reload and try again.";

    public ConfigPageTests()
    {
        ArrangeMaps(TestData.ConfigData());
        Confirmation.ConfirmAsync("Update ConfigMap", "gateway-config", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        Operations.ListSecretsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.Secrets());
    }

    private void ArrangeMaps(K8sConfigData data) =>
        Operations.ListConfigMapsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(_ => data);

    private static HttpOperationException Http(HttpStatusCode code, string message) => new("raw")
    {
        Response = new HttpResponseMessageWrapper(new HttpResponseMessage(code), $"{{\"kind\":\"Status\",\"message\":\"{message}\"}}"),
    };

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(string? query = "ns=checkout")
    {
        if (query is not null)
        {
            Services.GetRequiredService<NavigationManager>().NavigateTo($"/p/kubernetes/config?{query}");
        }

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<ConfigPage>(2);
            builder.CloseComponent();
        };

        var cut = Render(fragment);
        cut.WaitForState(() => cut.FindAll(".k8s-cm-row, .k8s-cm-empty, .k8s-cm-error").Count > 0
                               && cut.FindAll(".k8s-secret-row, .k8s-secrets-empty, .k8s-secrets-error").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".k8s-cm-row").Single(r => r.QuerySelector(".k8s-cm-name")!.TextContent.Trim() == name);

    private static AngleSharp.Dom.IElement SecretRow(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".k8s-secret-row").Single(r => r.QuerySelector(".k8s-secret-name")!.TextContent.Trim() == name);

    private static string Value(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string key)
    {
        var keys = cut.FindAll(".k8s-cm-kv .k8s-cm-key").ToList();
        var index = keys.FindIndex(k => k.TextContent.Trim() == key);
        index.Should().BeGreaterThanOrEqualTo(0, $"key {key} is listed");
        return cut.FindAll(".k8s-cm-kv .k8s-cm-value")[index].TextContent.Trim();
    }

    private static void Expand(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name)
    {
        Row(cut, name).Click();
        cut.WaitForAssertion(() => Row(cut, name).ClassList.Should().Contain("k8s-cm-row-expanded"));
    }

    [Fact]
    public void Header_title_and_card_counts_name_the_namespace_scope()
    {
        var cut = RenderPage();

        cut.Find(".k8s-header .k8s-title").TextContent.Should().Be("Config");
        cut.Find(".k8s-configmaps .k8s-cm-count").TextContent.Trim().Should().Be("3 in checkout");
        cut.Find(".k8s-secrets .k8s-secrets-count").TextContent.Trim().Should().Be("4 in checkout");
        cut.FindAll(".k8s-config-filter").Should().NotBeEmpty();
    }

    [Fact]
    public void All_namespaces_scope_is_named_as_such()
    {
        var cut = RenderPage(query: null);

        cut.Find(".k8s-cm-count").TextContent.Trim().Should().Be("3 in all namespaces");
    }

    [Fact]
    public void Config_map_rows_show_keys_users_and_age()
    {
        var cut = RenderPage();

        cut.FindAll(".k8s-cm-row").Select(r => r.QuerySelector(".k8s-cm-name")!.TextContent.Trim())
            .Should().Equal("gateway-config", "kube-root-ca.crt", "reports-config");
        var gateway = Row(cut, "gateway-config");
        gateway.QuerySelector(".k8s-cm-keys")!.TextContent.Trim().Should().Be("6");
        gateway.QuerySelector(".k8s-cm-used")!.TextContent.Trim().Should().Be("payment-gateway · env + volume");
        gateway.QuerySelector(".k8s-cm-age")!.TextContent.Trim().Should().Be("41d");
        Row(cut, "kube-root-ca.crt").QuerySelector(".k8s-cm-used")!.TextContent.Trim().Should().Be("all pods · projected");
        Row(cut, "reports-config").QuerySelector(".k8s-cm-keys")!.TextContent.Trim().Should().Be("3", "binary keys count too");
    }

    [Fact]
    public void Several_users_are_comma_joined()
    {
        var data = TestData.ConfigData();
        ArrangeMaps(data with { Refs = [.. data.Refs, new K8sConfigRef("checkout", "gateway-config", "checkout-api", "env")] });
        var cut = RenderPage();

        Row(cut, "gateway-config").QuerySelector(".k8s-cm-used")!.TextContent.Trim()
            .Should().Be("checkout-api · env, payment-gateway · env + volume");
    }

    [Fact]
    public void Unused_map_shows_the_unused_chip()
    {
        var cut = RenderPage();

        Row(cut, "reports-config").QuerySelector(".k8s-unused")!.TextContent.Trim().Should().Be("unused");
        Row(cut, "gateway-config").QuerySelector(".k8s-unused").Should().BeNull();
    }

    [Fact]
    public void Clicking_a_row_expands_the_key_value_grid_with_long_values_collapsed()
    {
        var cut = RenderPage();
        cut.FindAll(".k8s-cm-kv").Should().BeEmpty();

        Expand(cut, "gateway-config");

        cut.FindAll(".k8s-cm-kv").Should().ContainSingle();
        Value(cut, "REDIS_URL").Should().Be("redis://cart-redis:6379/0");
        Value(cut, "FEATURE_FLAGS").Should().Be("3ds2=on,apple_pay=on,klarna=off");
        Value(cut, "PROVIDERS_JSON").Should().Be($"{{…}} {K8sFormat.Bytes(TestData.ProvidersJson.Length)} · expand");
        Value(cut, "logging.yaml").Should().Be("14 lines · expand");
        cut.Markup.Should().NotContain("provider-22");
        cut.FindAll("pre.k8s-cm-pre").Should().BeEmpty();
    }

    [Fact]
    public void Expand_shows_the_whole_value_and_clicking_the_row_again_collapses()
    {
        var cut = RenderPage();
        Expand(cut, "gateway-config");

        var keys = cut.FindAll(".k8s-cm-kv .k8s-cm-key").ToList();
        var index = keys.FindIndex(k => k.TextContent.Trim() == "logging.yaml");
        cut.FindAll(".k8s-cm-kv .k8s-cm-value")[index].QuerySelector(".k8s-cm-expand")!.Click();

        cut.WaitForAssertion(() => cut.Find("pre.k8s-cm-pre").TextContent.Should().Be(TestData.LoggingYaml));

        Row(cut, "gateway-config").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-cm-kv").Should().BeEmpty());
    }

    [Fact]
    public void Binary_keys_show_their_size()
    {
        var cut = RenderPage();
        Expand(cut, "reports-config");

        Value(cut, "logo.png").Should().Be("2 Ki binary");
    }

    [Fact]
    public void Admin_sees_copy_and_edit_viewer_sees_copy_only_and_the_rbac_chip()
    {
        var cut = RenderPage();
        Expand(cut, "gateway-config");
        cut.FindAll(".k8s-cm-copy-yaml").Should().ContainSingle();
        cut.FindAll(".k8s-cm-edit").Should().ContainSingle();
        cut.FindAll(".k8s-rbac-chip").Should().BeEmpty();
    }

    [Fact]
    public void Viewer_has_no_edit()
    {
        Operations.GetAccessAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.ViewerAccess());
        var cut = RenderPage();
        Expand(cut, "gateway-config");

        cut.FindAll(".k8s-cm-copy-yaml").Should().ContainSingle();
        cut.FindAll(".k8s-cm-edit").Should().BeEmpty();
        cut.Find(".k8s-rbac-chip").TextContent.Should().Contain("read-only (RBAC)");
    }

    [Fact]
    public void Copy_as_yaml_copies_the_map_yaml()
    {
        const string yaml = "apiVersion: v1\nkind: ConfigMap\n";
        Operations.GetYamlAsync(Secret, K8sResourceKind.ConfigMap, "checkout", "gateway-config", Arg.Any<CancellationToken>()).Returns(yaml);
        var module = JSInterop.SetupModule(K8sJs.ModulePath);
        module.SetupVoid("copyText", yaml);
        var cut = RenderPage();
        Expand(cut, "gateway-config");

        cut.Find(".k8s-cm-copy-yaml").Click();

        cut.WaitForAssertion(() => module.VerifyInvoke("copyText").Arguments.Should().Equal(yaml));
    }

    [Fact]
    public void Filter_narrows_both_tables_by_name()
    {
        var cut = RenderPage();

        cut.Find(".k8s-config-filter input").Input("gateway");

        cut.WaitForAssertion(() => cut.FindAll(".k8s-cm-row").Should().ContainSingle());
        cut.FindAll(".k8s-secret-row").Should().BeEmpty();
        cut.Find(".k8s-config-filter input").Input("tls");
        cut.WaitForAssertion(() => cut.FindAll(".k8s-secret-row").Should().ContainSingle());
    }

    [Fact]
    public void Secrets_table_shows_type_key_chips_and_age_with_no_value_column()
    {
        var cut = RenderPage();

        cut.Find(".k8s-secrets-metadata").TextContent.Trim().Should().Be("metadata only");
        cut.Find(".k8s-secrets thead").TextContent.Should().NotContain("Value");
        var stripe = SecretRow(cut, "stripe-live");
        stripe.QuerySelector(".k8s-secret-type")!.TextContent.Trim().Should().Be("Opaque");
        stripe.QuerySelectorAll(".k8s-secret-key").Select(k => k.TextContent.Trim()).Should().Equal("STRIPE_SECRET_KEY", "STRIPE_WEBHOOK_SECRET");
        stripe.QuerySelector(".k8s-secret-age")!.TextContent.Trim().Should().Be("120d");
        stripe.QuerySelector(".k8s-tls-expiry").Should().BeNull();
    }

    [Fact]
    public void Callout_says_values_are_never_shown()
    {
        var cut = RenderPage();

        cut.Find(".k8s-secrets-callout").TextContent.Trim().Should().Be(
            "Secret values are never shown — the console reads key names, type and age only. Rotate or read values with your cluster's secret manager.");
    }

    [Theory]
    [InlineData(9, "expires in 9d", "mud-chip-color-warning")]
    [InlineData(45, "expires in 45d", "mud-chip-color-default")]
    [InlineData(3, "expires in 3d", "mud-chip-color-error")]
    [InlineData(-2, "expired", "mud-chip-color-error")]
    public void Tls_rows_show_an_expiry_chip_coloured_by_days_left(int days, string text, string colour)
    {
        Operations.ListSecretsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.Secrets().Select(s => s.Name == "checkout-tls" ? s with { TlsNotAfter = TestData.T0.AddDays(days) } : s).ToList());
        var cut = RenderPage();

        var chip = SecretRow(cut, "checkout-tls").QuerySelector(".k8s-tls-expiry")!;
        chip.TextContent.Trim().Should().Be(text);
        chip.ClassList.Should().Contain(colour);
        cut.FindAll(".k8s-tls-expiry").Should().ContainSingle();
    }

    [Fact]
    public void Forbidden_secrets_show_an_inline_message_while_config_maps_still_render()
    {
        Operations.ListSecretsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Http(HttpStatusCode.Forbidden,
                "secrets is forbidden: User \\\"viewer\\\" cannot list resource \\\"secrets\\\" in API group \\\"\\\" at the cluster scope"));
        var cut = RenderPage();

        cut.Find(".k8s-secrets-error").TextContent.Should().Contain("Your account can't list secrets.");
        cut.FindAll(".k8s-secret-row").Should().BeEmpty();
        cut.FindAll(".k8s-cm-row").Should().HaveCount(3);
    }

    [Fact]
    public void Failed_config_map_list_shows_an_error_while_secrets_still_render()
    {
        Operations.ListConfigMapsAsync(Secret, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Http(HttpStatusCode.InternalServerError, "boom"));
        var cut = RenderPage();

        cut.Find(".k8s-cm-error").TextContent.Should().Contain("The API server returned an error (500).");
        cut.FindAll(".k8s-secret-row").Should().HaveCount(4);
    }

    [Fact]
    public void Page_markup_never_contains_a_secret_value()
    {
        var cut = RenderPage();

        // The only Secret data the page holds is SecretSummary, which has nowhere to put a value: key names only.
        cut.Markup.Should().Contain("STRIPE_SECRET_KEY");
        cut.Markup.Should().NotContain("sk_live").And.NotContain("BEGIN PRIVATE KEY").And.NotContain("auths");
    }

    [Fact]
    public void Refresh_reloads_both_cards()
    {
        var cut = RenderPage();
        var before = Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.ListSecretsAsync));

        cut.Find(".k8s-config-refresh").Click();

        cut.WaitForAssertion(() => Operations.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.ListSecretsAsync)).Should().Be(before + 1));
        Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.ListConfigMapsAsync)).Should().Be(before + 1);
    }

    // ---- Edit dialog ----

    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> OpenEdit(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        Expand(cut, "gateway-config");
        cut.Find(".k8s-cm-edit").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-cm-edit-row").Should().HaveCount(6));
        return cut;
    }

    private static AngleSharp.Dom.IElement EditRow(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string key) =>
        cut.FindAll(".k8s-cm-edit-row").Single(r => r.QuerySelector(".k8s-cm-edit-key input")!.GetAttribute("value") == key);

    [Fact]
    public void Edit_dialog_lists_every_key_with_its_value()
    {
        var cut = OpenEdit(RenderPage());

        EditRow(cut, "CACHE_TTL_SECONDS").QuerySelector(".k8s-cm-edit-value textarea")!.TextContent.Should().Be("900");
        cut.Find(".k8s-cm-edit-save").HasAttribute("disabled").Should().BeTrue("nothing changed yet");
    }

    [Fact]
    public async Task Save_confirms_then_sends_the_original_and_new_data_and_reloads()
    {
        var cut = OpenEdit(RenderPage());

        EditRow(cut, "CACHE_TTL_SECONDS").QuerySelector(".k8s-cm-edit-value textarea")!.Input("1800");
        EditRow(cut, "FEATURE_FLAGS").QuerySelector(".k8s-cm-edit-remove")!.Click();
        cut.Find(".k8s-cm-edit-add").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-cm-edit-row").Should().HaveCount(6));
        var added = cut.FindAll(".k8s-cm-edit-row").Last();
        added.QuerySelector(".k8s-cm-edit-key input")!.Input("NEW_KEY");
        cut.FindAll(".k8s-cm-edit-row").Last().QuerySelector(".k8s-cm-edit-value textarea")!.Input("on");
        cut.Find(".k8s-cm-edit-save").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-cm-edit-dialog").Should().BeEmpty());
        await Confirmation.Received(1).ConfirmAsync("Update ConfigMap", "gateway-config", false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        var original = TestData.GatewayConfig().Data;
        await Operations.Received(1).UpdateConfigMapAsync(Secret, "checkout", "gateway-config",
            Arg.Is<IReadOnlyDictionary<string, string>>(d => d.Count == 6 && d["CACHE_TTL_SECONDS"] == "1800" && d["NEW_KEY"] == "on"
                                                              && !d.ContainsKey("FEATURE_FLAGS") && d["PROVIDERS_JSON"] == TestData.ProvidersJson),
            "1001", Arg.Any<CancellationToken>());
        await Audit.Received(1).RecordAsync("k8s.configmap.update", "aks-prod-eu/checkout/gateway-config", ActionRisk.Mutating, true,
            "added NEW_KEY · changed CACHE_TTL_SECONDS · removed FEATURE_FLAGS", Arg.Any<CancellationToken>());
        original.Should().ContainKey("FEATURE_FLAGS");
        cut.WaitForAssertion(() => Operations.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IKubernetesOperations.ListConfigMapsAsync)).Should().Be(2));
    }

    [Fact]
    public async Task Declining_the_confirmation_sends_nothing_and_keeps_the_dialog_open()
    {
        Confirmation.ConfirmAsync("Update ConfigMap", "gateway-config", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = OpenEdit(RenderPage());

        EditRow(cut, "CACHE_TTL_SECONDS").QuerySelector(".k8s-cm-edit-value textarea")!.Input("1800");
        cut.Find(".k8s-cm-edit-save").Click();

        await Confirmation.Received(1).ConfirmAsync("Update ConfigMap", "gateway-config", false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().UpdateConfigMapAsync(default!, default!, default!, default!, default!, default);
        cut.FindAll(".k8s-cm-edit-dialog").Should().ContainSingle();
    }

    [Fact]
    public void Duplicate_or_empty_keys_disable_save()
    {
        var cut = OpenEdit(RenderPage());

        EditRow(cut, "CACHE_TTL_SECONDS").QuerySelector(".k8s-cm-edit-key input")!.Input("REDIS_URL");

        cut.WaitForAssertion(() => cut.Find(".k8s-cm-edit-invalid").TextContent.Should().Contain("REDIS_URL"));
        cut.Find(".k8s-cm-edit-save").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void A_conflict_shows_the_changed_sentence_and_reload_resets_the_editor_to_the_latest_version()
    {
        Operations.UpdateConfigMapAsync(Secret, "checkout", "gateway-config", Arg.Any<IReadOnlyDictionary<string, string>>(), "1001", Arg.Any<CancellationToken>())
            .ThrowsAsync(Http(HttpStatusCode.Conflict, "the object has been modified"));
        var cut = OpenEdit(RenderPage());

        EditRow(cut, "CACHE_TTL_SECONDS").QuerySelector(".k8s-cm-edit-value textarea")!.Input("1800");
        cut.Find(".k8s-cm-edit-save").Click();

        cut.WaitForAssertion(() => cut.Find(".k8s-cm-edit-conflict").TextContent.Should().Contain(Changed));
        cut.FindAll(".k8s-cm-edit-dialog").Should().ContainSingle();

        // Someone else changed it: the reload brings their version (and its resourceVersion) into the editor.
        var latest = TestData.GatewayConfig() with
        {
            Data = new Dictionary<string, string>(TestData.GatewayConfig().Data) { ["CACHE_TTL_SECONDS"] = "1200" },
            ResourceVersion = "1002",
        };
        var data = TestData.ConfigData();
        ArrangeMaps(data with { ConfigMaps = [latest, .. data.ConfigMaps.Skip(1)] });
        cut.Find(".k8s-cm-edit-reload").Click();

        cut.WaitForAssertion(() => cut.FindAll(".k8s-cm-edit-conflict").Should().BeEmpty());
        EditRow(cut, "CACHE_TTL_SECONDS").QuerySelector(".k8s-cm-edit-value textarea")!.TextContent.Should().Be("1200");

        EditRow(cut, "CACHE_TTL_SECONDS").QuerySelector(".k8s-cm-edit-value textarea")!.Input("1800");
        cut.Find(".k8s-cm-edit-save").Click();
        cut.WaitForAssertion(() => cut.FindAll(".k8s-cm-edit-dialog").Should().BeEmpty());
        Operations.Received(1).UpdateConfigMapAsync(Secret, "checkout", "gateway-config",
            Arg.Is<IReadOnlyDictionary<string, string>>(d => d["CACHE_TTL_SECONDS"] == "1800"), "1002", Arg.Any<CancellationToken>());
    }
}
