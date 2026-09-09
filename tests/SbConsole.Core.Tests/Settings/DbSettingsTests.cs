using FluentAssertions;
using SbConsole.Core.Settings;

namespace SbConsole.Core.Tests.Settings;

public class DbSettingsTests
{
    [Fact]
    public async Task Get_returns_null_for_unknown_key()
    {
        using var testDb = new TestDb();
        var settings = new DbSettings(testDb);

        (await settings.GetAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task Set_then_get_roundtrips_and_set_overwrites()
    {
        using var testDb = new TestDb();
        var settings = new DbSettings(testDb);

        await settings.SetAsync("theme", "dark");
        await settings.SetAsync("theme", "light");

        (await settings.GetAsync("theme")).Should().Be("light");
    }
}
