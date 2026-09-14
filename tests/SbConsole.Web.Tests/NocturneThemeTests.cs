using FluentAssertions;
using MudBlazor.Utilities;
using SbConsole.Web.Theming;

namespace SbConsole.Web.Tests;

public class NocturneThemeTests
{
    [Fact]
    public void Dark_palette_matches_Nocturne_tokens()
    {
        var palette = NocturneTheme.Dark.PaletteDark;

        palette.Background.ToString(MudColorOutputFormats.Hex).Should().Be("#161826");
        palette.Surface.ToString(MudColorOutputFormats.Hex).Should().Be("#232532");
        palette.TextPrimary.ToString(MudColorOutputFormats.Hex).Should().Be("#e9e9ed");
        palette.Primary.ToString(MudColorOutputFormats.Hex).Should().Be("#9184d9");
        palette.AppbarText.ToString(MudColorOutputFormats.Hex).Should().Be("#e9e9ed");
        palette.DrawerText.ToString(MudColorOutputFormats.Hex).Should().Be("#e9e9ed");
    }

    [Fact]
    public void Light_palette_steps_the_accent_down_for_contrast_on_a_light_ground()
    {
        var palette = NocturneTheme.Light.PaletteLight;

        // Nocturne's own light-theme guidance: "surfaces lift instead of recede, and the
        // accent steps down to accent-700 to hold contrast on paper-white" — NOT the raw
        // #9184d9 accent, which under-contrasts on a light background.
        palette.Primary.ToString(MudColorOutputFormats.Hex).Should().Be("#5d5294");
        palette.Background.ToString(MudColorOutputFormats.Hex).Should().Be("#f3f5fe");
        palette.TextPrimary.ToString(MudColorOutputFormats.Hex).Should().Be("#292b31");
        palette.AppbarText.ToString(MudColorOutputFormats.Hex).Should().Be("#292b31");
        palette.DrawerText.ToString(MudColorOutputFormats.Hex).Should().Be("#292b31");
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
