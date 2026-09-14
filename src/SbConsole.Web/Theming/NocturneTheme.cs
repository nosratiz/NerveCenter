using MudBlazor;

namespace SbConsole.Web.Theming;

/// <summary>
/// MudBlazor theme derived from the Nocturne design system (docs/design.md §10).
/// Scope is theme tokens only — colors, typography, corner radius — not custom CSS
/// forcing MudBlazor components into Nocturne's specific component shapes.
/// Shadow customization (<see cref="MudTheme.Shadows"/>) is intentionally left at
/// MudBlazor's defaults — an explicit scope cut, not an oversight; see docs/design.md §10.
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
            AppbarText = "#e9e9ed",
            DrawerText = "#e9e9ed",
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
            AppbarText = "#292b31",
            DrawerText = "#292b31",
            TextSecondary = "#292b318c",
            Divider = "#292b3129",
            LinesDefault = "#292b3129",
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
