# Nocturne Theming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the Nocturne design system's tokens (colors, typography, corner radius, shadow tuning) as the app's MudBlazor theme, in both light and dark variants, applied consistently across every page including the pre-login screen.

**Architecture:** A `NocturneTheme` static class provides `Dark`/`Light` `MudTheme` instances built from Nocturne's tokens. The dark-mode/system-preference resolution logic currently duplicated only in `MainLayout` gets extracted into a shared `ThemedRoot` component that both `MainLayout` and `EmptyLayout` (the Login page's layout) wrap their content in, so every page — including pre-login — renders in the same resolved theme.

**Tech Stack:** MudBlazor 9.9.0 (already installed), bUnit + xUnit + FluentAssertions (already installed).

## Global Constraints

- .NET 10, C# `latest`, nullable enabled, warnings as errors (already enforced via `Directory.Build.props`).
- Gate before every commit: `dotnet build -warnaserror && dotnet test` both green.
- Scope is **theme tokens only** — no custom CSS overrides forcing MudBlazor components into Nocturne's specific shapes (outlined-only buttons, fading-gradient table rule, etc.). Components keep their native MudBlazor shape, just recolored/retyped.
- Nocturne source tokens (for reference, from the design system's `styles.css`):
  - `--color-bg: #161826`, `--color-surface: #232532`, `--color-text: #e9e9ed`
  - `--color-accent: #9184d9`, `--color-accent-400: #b5abfc`, `--color-accent-600: #796cbf`, `--color-accent-700: #5d5294`, `--color-accent-800: #423a6a`
  - `--color-neutral-100: #f3f5fe`, `--color-neutral-900: #292b31`
  - `--radius-md: 8px`, `--font-heading-weight: 500`
  - `--shadow-sm: 0 0 0 1px #3f424d`, `--shadow-md: 0 0 0 1px #595d6c, 0 6px 18px rgba(0,0,0,0.55)`, `--shadow-lg: 0 0 0 1px #9397ab, 0 16px 40px rgba(0,0,0,0.65)`
- `docs/design.md` §10 has the full rationale for every value below (why light-mode steps down to `accent-700`, why semantic colors are invented rather than sourced, etc.) — read it if a value below seems arbitrary.
- Conventional commits, ending with:
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>

## Known API-risk note (read before Task 1)

This project has repeatedly found that MudBlazor 9.9.0's exact public API differs from what's assumed in earlier MudBlazor versions or online docs (confirmed via `ilspycmd` decompilation multiple times already in this codebase — see the comment in `MainLayout.razor:40-42` about `GetSystemDarkModeAsync()`). Before writing `NocturneTheme.cs`, verify `MudTheme`'s actual property names (`PaletteLight`/`PaletteDark` vs alternatives) and the color property types on `PaletteDark`/`PaletteLight` (whether `Background`/`Primary`/etc. accept a plain string via implicit conversion, or require an explicit `MudColor`/`new MudColor("#...")` construction) by checking the installed package directly — e.g. `find ~/.nuget/packages/mudblazor -name "MudTheme.cs" -o -name "PaletteDark.cs" -o -name "PaletteLight.cs" 2>/dev/null` or decompiling `MudBlazor.dll` the way earlier tasks in this project did. Adapt the code below to match reality; the values (hex codes) are fixed by this plan, the exact C# property names/types are not.

---

### Task 1: NocturneTheme — Dark and Light MudTheme instances

**Files:**
- Create: `src/SbConsole.Web/Theming/NocturneTheme.cs`
- Test: `tests/SbConsole.Web.Tests/NocturneThemeTests.cs`

**Interfaces:**
- Consumes: nothing new (MudBlazor's own `MudTheme` type).
- Produces (used by Task 2): `public static class NocturneTheme { public static readonly MudTheme Dark; public static readonly MudTheme Light; }`.

- [ ] **Step 1: Verify the installed MudTheme API**

Run: `find ~/.nuget/packages/mudblazor -iname "*.dll" | head -1` to locate the installed package, then inspect `MudTheme`, `PaletteDark`, `PaletteLight`, `Typography`, `LayoutProperties` either via decompilation (`ilspycmd`, already used elsewhere in this project) or by writing a throwaway one-line test that intentionally references `new MudTheme { PaletteDark = new PaletteDark { Background = "#161826" } }` and reading the compiler's error/success to confirm the property and assignment shape. Note what you find — later steps assume `PaletteDark`/`PaletteLight` properties exist on `MudTheme` and that color properties accept a plain hex string; adjust if reality differs.

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/SbConsole.Web.Tests/NocturneThemeTests.cs
using FluentAssertions;
using SbConsole.Web.Theming;

namespace SbConsole.Web.Tests;

public class NocturneThemeTests
{
    [Fact]
    public void Dark_palette_matches_Nocturne_tokens()
    {
        var palette = NocturneTheme.Dark.PaletteDark;

        palette.Background.ToString().Should().Be("#161826");
        palette.Surface.ToString().Should().Be("#232532");
        palette.TextPrimary.ToString().Should().Be("#e9e9ed");
        palette.Primary.ToString().Should().Be("#9184d9");
    }

    [Fact]
    public void Light_palette_steps_the_accent_down_for_contrast_on_a_light_ground()
    {
        var palette = NocturneTheme.Light.PaletteLight;

        // Nocturne's own light-theme guidance: "surfaces lift instead of recede, and the
        // accent steps down to accent-700 to hold contrast on paper-white" — NOT the raw
        // #9184d9 accent, which under-contrasts on a light background.
        palette.Primary.ToString().Should().Be("#5d5294");
        palette.Background.ToString().Should().Be("#f3f5fe");
        palette.TextPrimary.ToString().Should().Be("#292b31");
    }

    [Fact]
    public void Both_palettes_define_all_four_semantic_colors()
    {
        NocturneTheme.Dark.PaletteDark.Error.ToString().Should().NotBeNullOrEmpty();
        NocturneTheme.Dark.PaletteDark.Warning.ToString().Should().NotBeNullOrEmpty();
        NocturneTheme.Dark.PaletteDark.Success.ToString().Should().NotBeNullOrEmpty();
        NocturneTheme.Dark.PaletteDark.Info.ToString().Should().NotBeNullOrEmpty();
        NocturneTheme.Light.PaletteLight.Error.ToString().Should().NotBeNullOrEmpty();
        NocturneTheme.Light.PaletteLight.Warning.ToString().Should().NotBeNullOrEmpty();
        NocturneTheme.Light.PaletteLight.Success.ToString().Should().NotBeNullOrEmpty();
        NocturneTheme.Light.PaletteLight.Info.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Corner_radius_matches_Nocturne_radius_md()
    {
        NocturneTheme.Dark.LayoutProperties.DefaultBorderRadius.Should().Be("8px");
        NocturneTheme.Light.LayoutProperties.DefaultBorderRadius.Should().Be("8px");
    }
}
```

Adjust `.ToString()` calls if the verified color property type (Step 1) doesn't round-trip to the hex string via `ToString()` — e.g. if it's a `MudColor`, confirm what its `ToString()` actually produces (it may include an alpha channel or different casing) and match the assertion to that, not the other way around.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter NocturneThemeTests`
Expected: FAIL to compile — `NocturneTheme` does not exist.

- [ ] **Step 4: Write NocturneTheme.cs**

```csharp
// src/SbConsole.Web/Theming/NocturneTheme.cs
using MudBlazor;

namespace SbConsole.Web.Theming;

/// <summary>
/// MudBlazor theme derived from the Nocturne design system (docs/design.md §10).
/// Scope is theme tokens only — colors, typography, corner radius, shadow tuning —
/// not custom CSS forcing MudBlazor components into Nocturne's specific component shapes.
/// </summary>
public static class NocturneTheme
{
    private const string FontFamily = "Inter";
    private static readonly string[] FontStack = [FontFamily, "system-ui", "sans-serif"];

    public static readonly MudTheme Dark = new()
    {
        PaletteDark = new PaletteDark
        {
            Background = "#161826",
            Surface = "#232532",
            AppbarBackground = "#161826",
            DrawerBackground = "#161826",
            Primary = "#9184d9",
            PrimaryDarken = "#796cbf",
            PrimaryLighten = "#b5abfc",
            TextPrimary = "#e9e9ed",
            TextSecondary = "#e9e9ed8c", // ~55% alpha over TextPrimary, mirroring Nocturne's color-mix(text 55%, transparent)
            Divider = "#e9e9ed29",       // ~16% alpha, mirroring Nocturne's color-mix(text 16%, transparent)
            LinesDefault = "#e9e9ed29",
            // Semantic colors have no source in Nocturne (it's a single-accent system) —
            // chosen here, muted to match Nocturne's low-chroma-outside-the-accent rule.
            Error = "#e5738c",
            Warning = "#d9a25c",
            Success = "#7fb88f",
            Info = "#7aa8d9",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontStack },
            H1 = new H1Typography { FontFamily = FontStack, FontWeight = "500" },
            H2 = new H2Typography { FontFamily = FontStack, FontWeight = "500" },
            H3 = new H3Typography { FontFamily = FontStack, FontWeight = "500" },
            H4 = new H4Typography { FontFamily = FontStack, FontWeight = "500" },
            H5 = new H5Typography { FontFamily = FontStack, FontWeight = "500" },
            H6 = new H6Typography { FontFamily = FontStack, FontWeight = "500" },
            Body1 = new Body1Typography { FontFamily = FontStack },
            Body2 = new Body2Typography { FontFamily = FontStack },
            Button = new ButtonTypography { FontFamily = FontStack },
            Caption = new CaptionTypography { FontFamily = FontStack },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
    };

    public static readonly MudTheme Light = new()
    {
        PaletteLight = new PaletteLight
        {
            Background = "#f3f5fe",
            Surface = "#ffffff", // surfaces lift toward white on the light ground, per Nocturne's own light-theme note
            AppbarBackground = "#f3f5fe",
            DrawerBackground = "#f3f5fe",
            Primary = "#5d5294", // accent-700 — Nocturne's own guidance: step the accent down for contrast on paper-white
            PrimaryDarken = "#423a6a",
            PrimaryLighten = "#796cbf",
            TextPrimary = "#292b31",
            TextSecondary = "#292b318c",
            Divider = "#29283129",
            LinesDefault = "#29283129",
            Error = "#c94f68",
            Warning = "#b9803a",
            Success = "#4f8f63",
            Info = "#4f7fb9",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontStack },
            H1 = new H1Typography { FontFamily = FontStack, FontWeight = "500" },
            H2 = new H2Typography { FontFamily = FontStack, FontWeight = "500" },
            H3 = new H3Typography { FontFamily = FontStack, FontWeight = "500" },
            H4 = new H4Typography { FontFamily = FontStack, FontWeight = "500" },
            H5 = new H5Typography { FontFamily = FontStack, FontWeight = "500" },
            H6 = new H6Typography { FontFamily = FontStack, FontWeight = "500" },
            Body1 = new Body1Typography { FontFamily = FontStack },
            Body2 = new Body2Typography { FontFamily = FontStack },
            Button = new ButtonTypography { FontFamily = FontStack },
            Caption = new CaptionTypography { FontFamily = FontStack },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
    };
}
```

This is a strong starting point, not gospel — reconcile it against what Step 1 actually found. Likely adaptation points, roughly in order of likelihood: the per-variant typography class names (`H1Typography`, `DefaultTypography`, etc. — MudBlazor may use a single generic settings class instead, or different names); whether `FontWeight` on a typography class is `string`, `int`, or `int?`; whether `PaletteDark`/`PaletteLight` are the right class names (vs., e.g., both being plain `Palette` with the theme's `PaletteDark`/`PaletteLight` properties just being of type `Palette`). Shadow customization (`MudTheme.Shadows`/`Shadow.Elevation`) is explicitly OPTIONAL for this task — attempt it only if the API is straightforward; if it needs significant unplanned effort, skip it, leave MudBlazor's default shadows, and note this as a concern in your report. Getting the palette, typography font family/weight, and border radius right matters far more than shadow fidelity for this task.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter NocturneThemeTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add NocturneTheme (dark/light MudTheme instances)"
```

---

### Task 2: ThemedRoot — shared dark-mode resolution, wired into both layouts

**Files:**
- Create: `src/SbConsole.Web/Theming/ThemedRoot.razor`
- Modify: `src/SbConsole.Web/Components/Layout/MainLayout.razor`, `src/SbConsole.Web/Components/Layout/EmptyLayout.razor`
- Test: `tests/SbConsole.Web.Tests/ThemedRootTests.cs`

**Interfaces:**
- Consumes: `NocturneTheme.Dark`/`.Light` (Task 1), `ISettings` (Plan 1 Task 6), `MudThemeProvider.GetSystemDarkModeAsync()` (already verified working in this codebase — see `MainLayout.razor`'s existing comment).
- Produces: `ThemedRoot` — a component with a `ChildContent` `RenderFragment` parameter; resolves `theme.mode` from `ISettings` the same way `MainLayout` currently does (dark → true, light/unset-non-system → false, system → refined post-render via `GetSystemDarkModeAsync()`), and renders `<MudThemeProvider Theme="@(_isDarkMode ? NocturneTheme.Dark : NocturneTheme.Light)" @bind-IsDarkMode="_isDarkMode" @ref="_themeProvider" />` followed by `@ChildContent`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Web.Tests/ThemedRootTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Core.Data;
using SbConsole.Core.Settings;
using SbConsole.Core.Tests;
using SbConsole.Web.Theming;

namespace SbConsole.Web.Tests;

public class ThemedRootTests : BunitContext, IAsyncLifetime
{
    private readonly TestDb _testDb = new();

    public ThemedRootTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ISettings, DbSettings>();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    [Fact]
    public async Task Renders_child_content_regardless_of_theme()
    {
        var cut = Render<ThemedRoot>(parameters => parameters
            .AddChildContent("<p>hello</p>"));
        await Task.Delay(20);
        cut.Render();

        cut.Markup.Should().Contain("hello");
    }

    [Fact]
    public async Task Dark_setting_resolves_to_dark_immediately()
    {
        await Services.GetRequiredService<ISettings>().SetAsync("theme.mode", "dark");

        var cut = Render<ThemedRoot>(parameters => parameters
            .AddChildContent("<p>hello</p>"));
        await Task.Delay(20);
        cut.Render();

        cut.Markup.Should().Contain("hello"); // renders without throwing under the dark theme
    }

    [Fact]
    public async Task Light_setting_resolves_to_light_immediately()
    {
        await Services.GetRequiredService<ISettings>().SetAsync("theme.mode", "light");

        var cut = Render<ThemedRoot>(parameters => parameters
            .AddChildContent("<p>hello</p>"));
        await Task.Delay(20);
        cut.Render();

        cut.Markup.Should().Contain("hello");
    }
}
```

This test intentionally checks that `ThemedRoot` renders its child content correctly under each setting rather than reaching into MudBlazor's internal dark-mode state (which isn't easily observable from outside without the same kind of internal-API workaround Task 5/6 of the Core UI plan needed for `MudDialog` — not worth repeating here for a theme-selection component whose main risk is "does it crash," not "is the boolean flipped"). If you find a clean, non-fragile way to assert the resolved `IsDarkMode` value directly (e.g. `cut.Instance` exposing it, if you make the backing field internal/testable), feel free to add that as a stronger assertion — but don't spend excessive effort chasing it if it requires another internal-API workaround.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Web.Tests --filter ThemedRootTests`
Expected: FAIL to compile — `ThemedRoot` does not exist.

- [ ] **Step 3: Write ThemedRoot.razor**

```razor
@* src/SbConsole.Web/Theming/ThemedRoot.razor *@
@using MudBlazor
@using SbConsole.Core.Settings
@inject ISettings Settings

<MudThemeProvider Theme="@(_isDarkMode ? NocturneTheme.Dark : NocturneTheme.Light)" @bind-IsDarkMode="_isDarkMode" @ref="_themeProvider" />
@ChildContent

@code {
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private MudThemeProvider _themeProvider = default!;
    private bool _isDarkMode;

    protected override async Task OnInitializedAsync()
    {
        var theme = await Settings.GetAsync("theme.mode") ?? "system";
        _isDarkMode = theme == "dark";
        // "light" and unrecognized values already default to false above; "system" is
        // refined in OnAfterRenderAsync once the theme provider can report the OS preference.
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var theme = await Settings.GetAsync("theme.mode") ?? "system";
            if (theme == "system")
            {
                _isDarkMode = await _themeProvider.GetSystemDarkModeAsync();
                StateHasChanged();
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter ThemedRootTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Wire MainLayout.razor to use ThemedRoot**

Replace the entire contents of `src/SbConsole.Web/Components/Layout/MainLayout.razor` with:

```razor
@inherits LayoutComponentBase
@using SbConsole.Web.Theming

<ThemedRoot>
    <MudPopoverProvider />
    <MudDialogProvider />
    <MudSnackbarProvider />

    <MudLayout>
        <MudAppBar Elevation="1">
            <MudText Typo="Typo.h6">SbConsole</MudText>
        </MudAppBar>
        <MudDrawer Open="true" Elevation="1">
            <NavMenu />
        </MudDrawer>
        <MudMainContent Class="pa-4">
            @Body
        </MudMainContent>
    </MudLayout>
</ThemedRoot>
```

(The `@code` block with `_isDarkMode`/`_themeProvider`/`OnInitializedAsync`/`OnAfterRenderAsync` is gone entirely — that logic now lives only in `ThemedRoot`.)

- [ ] **Step 6: Wire EmptyLayout.razor to use ThemedRoot**

Replace the entire contents of `src/SbConsole.Web/Components/Layout/EmptyLayout.razor` with:

```razor
@* src/SbConsole.Web/Components/Layout/EmptyLayout.razor *@
@inherits LayoutComponentBase
@using SbConsole.Web.Theming

<ThemedRoot>
    @Body
</ThemedRoot>
```

- [ ] **Step 7: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green. Existing tests that render `MainLayout` or `EmptyLayout` directly (check `tests/SbConsole.Web.Tests/` for any) may need their DI setup extended to include `ISettings`/`IDbContextFactory<SbcDbContext>` registrations if they don't already have them, since `ThemedRoot` now injects `ISettings` wherever either layout renders — check and fix any resulting failures the same way earlier tasks in this project handled newly-required `[Inject]` dependencies in test hosts.

- [ ] **Step 8: Smoke-test manually**

```bash
SBC_DB_PATH=/tmp/sbc-theme-check.db \
SBC_DATA_KEY=$(head -c 32 /dev/urandom | base64) \
SBC_ADMIN_PASSWORD=dev \
SBC_API_KEY=dev-key \
dotnet run --project src/SbConsole.Web --no-launch-profile &
sleep 8
curl -s -o /dev/null -w "login:%{http_code}\n" http://localhost:5080/login
curl -s -o /dev/null -w "root:%{http_code}\n" -L http://localhost:5080/
kill %1
```

Expected: both `200`.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: extract shared ThemedRoot, apply NocturneTheme to both layouts (including Login)"
```

---

### Task 3: Load Inter font

**Files:**
- Modify: `src/SbConsole.Web/Components/App.razor`

**Interfaces:**
- Consumes: nothing.
- Produces: Inter available as a web font wherever `NocturneTheme`'s `FontFamily`/`FontStack` reference it (Task 1).

- [ ] **Step 1: Add the Inter font link**

Modify `src/SbConsole.Web/Components/App.razor` — add the font link before the existing MudBlazor CSS link:

```razor
    <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap" />
    <link rel="stylesheet" href="@Assets["_content/MudBlazor/MudBlazor.min.css"]" />
```

(This mirrors exactly how Nocturne's own `styles.css` loads Inter: `@import url('https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap');`.)

- [ ] **Step 2: Run the full gate**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green (this is a markup-only change with no test surface of its own).

- [ ] **Step 3: Visually confirm**

```bash
SBC_DB_PATH=/tmp/sbc-theme-check2.db \
SBC_DATA_KEY=$(head -c 32 /dev/urandom | base64) \
SBC_ADMIN_PASSWORD=dev \
SBC_API_KEY=dev-key \
dotnet run --project src/SbConsole.Web --no-launch-profile &
sleep 8
curl -s http://localhost:5080/login | grep -o "fonts.googleapis.com[^\"]*" | head -1
kill %1
```

Expected: prints the Inter font URL, confirming the link is present in the rendered page.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: load Inter font"
```

---

## After this plan

Deliberately out of scope, per the "theme tokens only" decision recorded in `docs/design.md` §10: custom CSS forcing MudBlazor components into Nocturne's specific shapes (outlined-only buttons, the fading-gradient table row rule, tag-chip styling). Revisit if a future review of the shipped app decides token-level theming isn't close enough to the mockups.
