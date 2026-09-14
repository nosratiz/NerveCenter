# Host Pages Modernization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring Login.razor and Home.razor's dashboard tiles off the leftover Material look (shadowed cards, unstyled native form controls) and onto the flat, borderless Nocturne convention already shipped for the sidebar, app-bar, and Queues page; cap Settings.razor's form field widths to match the input-width convention used elsewhere.

**Architecture:** Three independent, visual-only changes to existing Razor pages — no new components, no behavior/routing changes. Login gets a new scoped stylesheet (`Login.razor.css`) to restyle its native (non-Blazor) form controls; Home's dashboard tile `MudPaper` drops its shadow for a themed border; Settings' input fields get a `max-width`. Each change is verified by a small bUnit regression test asserting the resulting markup (CSS class, style string, or attribute), plus a manual browser check at the end covering all three together.

**Tech Stack:** Blazor Web App (Interactive Server), MudBlazor 9.9.0, bUnit + xUnit + FluentAssertions.

## Global Constraints

- Visual/CSS changes only — no behavior, routing, or functional changes (spec §2).
- `dotnet build -warnaserror` and `dotnet test` must be green before every commit (docs/design.md §8).
- TDD: write the failing test before the implementation, per task (docs/design.md §8).
- Do not change the `dashboard-tile` CSS class name on Home's tiles — `HomeTests.cs` asserts on it (spec §4).
- Do not change Login's `<form method="post" action="/auth/login">`, its `name="password"` input, or the password-only/no-username shape — `AuthEndpointsTests.cs` posts directly to this endpoint (spec §3).
- No changes to Connections.razor, Audit.razor, or Plugins.razor — already consistent with the target style (spec §2).

---

## Task 1: Flatten Login's card and restyle its native form controls

**Files:**
- Create: `src/SbConsole.Web/Components/Pages/Login.razor.css`
- Modify: `src/SbConsole.Web/Components/Pages/Login.razor`
- Test: `tests/SbConsole.Web.Tests/LoginTests.cs` (new file)

**Interfaces:**
- Consumes: MudBlazor CSS custom properties already used elsewhere in this codebase — `--mud-palette-lines-inputs`, `--mud-palette-primary`, `--mud-palette-primary-hover`, `--mud-palette-primary-darken`, `--mud-palette-primary-text`, `--mud-palette-surface`, `--mud-palette-text-primary`, `--mud-default-borderradius` (all confirmed present in `MudBlazor.min.css` 9.9.0).
- Produces: nothing consumed by later tasks (Login is not touched by Task 2 or 3).

- [x] **Step 1: Write the failing test**

Create `tests/SbConsole.Web.Tests/LoginTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using MudBlazor.Services;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class LoginTests : BunitContext
{
    public LoginTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Card_is_flat_with_no_shadow()
    {
        var cut = Render<Login>();

        var paper = cut.Find(".mud-paper");
        paper.ClassList.Should().Contain("mud-elevation-0");
        paper.ClassList.Should().NotContain("mud-elevation-1");
    }

    [Fact]
    public void Password_field_and_submit_button_keep_their_form_wiring()
    {
        var cut = Render<Login>();

        var form = cut.Find("form");
        form.GetAttribute("method").Should().Be("post");
        form.GetAttribute("action").Should().Be("/auth/login");

        var input = cut.Find("input[name=password]");
        input.GetAttribute("type").Should().Be("password");
        input.ClassList.Should().Contain("login-password");

        var button = cut.Find("button[type=submit]");
        button.ClassList.Should().Contain("login-submit");
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~LoginTests`

Expected: both tests FAIL — `Card_is_flat_with_no_shadow` because the `MudPaper` has no `Elevation` set (defaults to `mud-elevation-1`, not `mud-elevation-0`), and `Password_field_and_submit_button_keep_their_form_wiring` because the input/button don't have the `login-password`/`login-submit` classes yet.

- [x] **Step 3: Implement — flatten the card and add styling hooks to the native controls**

Replace the full contents of `src/SbConsole.Web/Components/Pages/Login.razor` with:

```razor
@* src/SbConsole.Web/Components/Pages/Login.razor *@
@page "/login"
@attribute [Microsoft.AspNetCore.Authorization.AllowAnonymous]
@layout SbConsole.Web.Components.Layout.EmptyLayout

<PageTitle>Sign in — SbConsole</PageTitle>

<div style="display:flex;justify-content:center;margin-top:15vh">
    <MudPaper Class="pa-8" Style="width:360px" Elevation="0">
        <MudText Typo="Typo.h5" Class="mb-4">SbConsole</MudText>
        <MudText Typo="Typo.body2" Class="mb-4">Self-hosted infrastructure console</MudText>
        <form method="post" action="/auth/login">
            <input type="password" name="password" placeholder="Admin password" class="login-password" autofocus />
            <button type="submit" class="login-submit">Sign in</button>
        </form>
        <MudText Typo="Typo.caption" Class="mt-2">Single shared account · sessions are audited</MudText>
        @if (Failed)
        {
            <MudText Color="Color.Error" Class="mt-2">Wrong password.</MudText>
        }
    </MudPaper>
</div>

@code {
    [SupplyParameterFromQuery(Name = "failed")]
    public bool Failed { get; set; }
}
```

This keeps the native, non-interactive `<form>`/`<input>`/`<button>` — it's a plain HTTP POST handled by a minimal API endpoint, not an interactive Blazor form, so it must not become `MudTextField`/`MudButton`. Only the inline `style="..."` attributes are replaced with `class` hooks for the new stylesheet below.

Create `src/SbConsole.Web/Components/Pages/Login.razor.css`:

```css
/* src/SbConsole.Web/Components/Pages/Login.razor.css */
.login-password {
    box-sizing: border-box;
    width: 100%;
    margin-bottom: 12px;
    padding: 10px 12px;
    font: inherit;
    color: var(--mud-palette-text-primary);
    background: var(--mud-palette-surface);
    border: 1px solid var(--mud-palette-lines-inputs);
    border-radius: var(--mud-default-borderradius);
}

.login-password:focus {
    outline: none;
    border-color: var(--mud-palette-primary);
    box-shadow: 0 0 0 2px var(--mud-palette-primary-hover);
}

.login-submit {
    box-sizing: border-box;
    width: 100%;
    padding: 10px 12px;
    font: inherit;
    font-weight: 600;
    color: var(--mud-palette-primary-text);
    background: var(--mud-palette-primary);
    border: none;
    border-radius: var(--mud-default-borderradius);
    cursor: pointer;
}

.login-submit:hover {
    background: var(--mud-palette-primary-darken);
}
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~LoginTests`

Expected: both tests PASS.

- [x] **Step 5: Run the full build and test gate**

Run: `dotnet build -warnaserror && dotnet test`

Expected: build succeeds with no warnings/errors; all tests pass (including the pre-existing `AuthEndpointsTests`, unaffected since it posts directly to `/auth/login` and never renders `Login.razor`).

- [x] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Components/Pages/Login.razor src/SbConsole.Web/Components/Pages/Login.razor.css tests/SbConsole.Web.Tests/LoginTests.cs
git commit -m "$(cat <<'EOF'
fix: flatten Login's card and theme its native form controls

MudPaper now matches the rest of the app's shadow-free cards
(Elevation=0), and the native password input/submit button — kept
native since this is a real POST form, not an interactive Blazor
one — pick up the theme's colors, radius, and a focus ring instead
of rendering as unstyled HTML.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Flatten Home's dashboard tiles

**Files:**
- Modify: `src/SbConsole.Web/Components/Pages/Home.razor:28`
- Test: `tests/SbConsole.Web.Tests/HomeTests.cs`

**Interfaces:**
- Consumes: `--mud-palette-lines-default` (confirmed present in `MudBlazor.min.css` 9.9.0).
- Produces: nothing consumed by later tasks.

- [x] **Step 1: Write the failing test**

Add to `tests/SbConsole.Web.Tests/HomeTests.cs`, inside the `HomeTests` class (after `Connections_tile_reflects_the_saved_connection_count`):

```csharp
    [Fact]
    public async Task Dashboard_tiles_are_flat_with_no_shadow()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-dev", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".dashboard-tile").Count > 0);

        var tile = cut.Find(".dashboard-tile");
        tile.ClassList.Should().Contain("mud-elevation-0");
        tile.ClassList.Should().NotContain("mud-elevation-1");
    }
```

- [x] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~Dashboard_tiles_are_flat_with_no_shadow`

Expected: FAIL — the tile currently renders with `Elevation="1"`, so it has class `mud-elevation-1`, not `mud-elevation-0`.

- [x] **Step 3: Implement — drop the shadow, add a themed border**

In `src/SbConsole.Web/Components/Pages/Home.razor`, change line 28 from:

```razor
        <MudPaper Class="dashboard-tile pa-4" Style="min-width:160px" Elevation="1">
```

to:

```razor
        <MudPaper Class="dashboard-tile pa-4" Style="min-width:160px;border:1px solid var(--mud-palette-lines-default)" Elevation="0">
```

- [x] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~Dashboard_tiles_are_flat_with_no_shadow`

Expected: PASS.

- [x] **Step 5: Run the full build and test gate**

Run: `dotnet build -warnaserror && dotnet test`

Expected: build succeeds; all tests pass, including the pre-existing `Connections_tile_reflects_the_saved_connection_count` (unaffected — it asserts on tile content, not elevation).

- [x] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Components/Pages/Home.razor tests/SbConsole.Web.Tests/HomeTests.cs
git commit -m "$(cat <<'EOF'
fix: flatten Home's dashboard tiles to match the flat card convention

Dashboard tiles were the only place among the host pages still using
a shadowed MudPaper (Elevation=1); every other page already uses flat,
borderless surfaces. Drops to Elevation=0 with a themed 1px border so
the tiles stay visually distinct without the shadow.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Cap Settings' form field widths

**Files:**
- Modify: `src/SbConsole.Web/Components/Pages/Settings.razor`
- Test: `tests/SbConsole.Web.Tests/SettingsPageTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing consumed by later tasks.

- [x] **Step 1: Write the failing test**

Add to `tests/SbConsole.Web.Tests/SettingsPageTests.cs`, inside the `SettingsPageTests` class (after `Saving_persists_instance_name_and_theme`):

```csharp
    [Fact]
    public void Form_fields_are_width_capped_for_visual_consistency()
    {
        var cut = Render<Settings>();

        cut.Markup.Should().Contain("max-width:320px");
    }
```

- [x] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~Form_fields_are_width_capped_for_visual_consistency`

Expected: FAIL — none of Settings' fields currently set a `max-width`.

- [x] **Step 3: Implement — cap the text/select/number field widths**

In `src/SbConsole.Web/Components/Pages/Settings.razor`, replace:

```razor
<MudTextField id="instance-name" @bind-Value="_instanceName" Label="Instance name" Immediate="true" />
<MudSelect T="string" @bind-Value="_theme" Label="Theme">
    <MudSelectItem Value="@("light")">Light</MudSelectItem>
    <MudSelectItem Value="@("dark")">Dark</MudSelectItem>
    <MudSelectItem Value="@("system")">System</MudSelectItem>
</MudSelect>
<MudNumericField T="int" @bind-Value="_sessionTimeoutHours" Label="Session timeout (hours)" />

<MudText Typo="Typo.h6" Class="mb-2 mt-6">Safety</MudText>
<MudSwitch T="bool" @bind-Value="_requireTypedConfirmation" Label="Require typed confirmation on prod-tagged targets" />
<MudNumericField T="int" @bind-Value="_auditRetentionDays" Label="Audit retention (days)" />
```

with:

```razor
<MudTextField id="instance-name" @bind-Value="_instanceName" Label="Instance name" Immediate="true" Style="max-width:320px" />
<MudSelect T="string" @bind-Value="_theme" Label="Theme" Style="max-width:320px">
    <MudSelectItem Value="@("light")">Light</MudSelectItem>
    <MudSelectItem Value="@("dark")">Dark</MudSelectItem>
    <MudSelectItem Value="@("system")">System</MudSelectItem>
</MudSelect>
<MudNumericField T="int" @bind-Value="_sessionTimeoutHours" Label="Session timeout (hours)" Style="max-width:320px" />

<MudText Typo="Typo.h6" Class="mb-2 mt-6">Safety</MudText>
<MudSwitch T="bool" @bind-Value="_requireTypedConfirmation" Label="Require typed confirmation on prod-tagged targets" />
<MudNumericField T="int" @bind-Value="_auditRetentionDays" Label="Audit retention (days)" Style="max-width:320px" />
```

(`MudSwitch` is a compact toggle, not a text-entry field, so it's left uncapped — matching why filter/text inputs elsewhere are capped but buttons and switches aren't.)

- [x] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Web.Tests --filter FullyQualifiedName~Form_fields_are_width_capped_for_visual_consistency`

Expected: PASS.

- [x] **Step 5: Run the full build and test gate**

Run: `dotnet build -warnaserror && dotnet test`

Expected: build succeeds; all tests pass, including the pre-existing `Saving_persists_instance_name_and_theme` (unaffected — it interacts with `#instance-name` and `.save-settings`, neither of which changed).

- [x] **Step 6: Commit**

```bash
git add src/SbConsole.Web/Components/Pages/Settings.razor tests/SbConsole.Web.Tests/SettingsPageTests.cs
git commit -m "$(cat <<'EOF'
fix: cap Settings' form field widths for visual consistency

Settings' text/select/number fields stretched full-width, unlike the
capped filter/input fields used elsewhere (Queues, Connections). Caps
them at 320px; the switch is left uncapped since it's a compact
toggle, not a text-entry field.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Manual browser verification

**Files:** none (verification only).

**Interfaces:** none.

- [x] **Step 1: Start the app**

Run: `dotnet run --project src/SbConsole.Web --launch-profile http`

(If a dev instance is already running on port 5249, reuse it instead of starting a second one.)

- [x] **Step 2: Verify Login**

Open `http://localhost:5249/login`. Confirm:
- The card has no drop shadow.
- The password field and "Sign in" button are rounded, themed (not plain unstyled HTML), and the field shows an accent-colored focus ring when tabbed into.
- Signing in with the correct admin password (from `SBC_ADMIN_PASSWORD` in `launchSettings.json`) still redirects past login.
- Signing in with a wrong password still shows "Wrong password." and keeps the same styling.

- [x] **Step 3: Verify Home**

Open `http://localhost:5249/`. Confirm the dashboard tiles read as flat, bordered blocks with no shadow, in both light and dark theme (toggle via Settings → Theme).

- [x] **Step 4: Verify Settings**

Open `http://localhost:5249/settings`. Confirm the Instance name, Theme, Session timeout, and Audit retention fields no longer stretch full-width, and Save/Discard still work.

- [x] **Step 5: Confirm no regressions on the untouched pages**

Open Connections, Audit, and Plugins. Confirm they look unchanged from before this plan.

No commit for this task — it's a verification-only step confirming Tasks 1-3 together.
