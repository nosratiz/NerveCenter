using System.Globalization;
using Bunit;
using FluentAssertions;
using SbConsole.Plugins.Redis.Components;

namespace SbConsole.Plugins.Redis.Tests.Components;

public class RedisSparklineTests : BunitContext
{
    private IReadOnlyList<double> Ys(params double[] values)
    {
        var cut = Render<RedisSparkline>(p => p.Add(x => x.Values, values));
        return cut.Find("polyline").GetAttribute("points")!
            .Split(' ')
            .Select(point => double.Parse(point.Split(',')[1], CultureInfo.InvariantCulture))
            .ToList();
    }

    [Fact]
    public void Renders_nothing_below_two_points() =>
        Render<RedisSparkline>(p => p.Add(x => x.Values, new double[] { 5 })).Markup.Trim().Should().BeEmpty();

    [Fact]
    public void A_flat_series_lies_along_the_bottom() =>
        Ys(0, 0, 0).Should().AllSatisfy(y => y.Should().Be(28));

    [Fact]
    public void The_highest_point_is_drawn_highest() =>
        Ys(10, 50, 20)[1].Should().BeLessThan(Ys(10, 50, 20)[0]);

    [Fact]
    public void A_tiny_wobble_on_a_large_value_stays_a_small_movement()
    {
        // 97.7% -> 98.0% hit ratio must not swing the full height of the tile.
        var ys = Ys(97.7, 98.0);

        (ys[0] - ys[1]).Should().BeLessThan(5);
    }
}
