using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class K8sFormatTests
{
    [Theory]
    [InlineData(45, "45s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m")]
    [InlineData(360, "6m")]
    [InlineData(7 * 3600 + 5, "7h")]
    [InlineData(41 * 86400, "41d")]
    [InlineData(729 * 86400, "729d")]
    [InlineData(730 * 86400, "2y")]
    [InlineData(1200 * 86400, "3y")]
    public void Age_floors(int seconds, string expected) =>
        K8sFormat.Age(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void Ago_is_just_now_under_five_seconds()
    {
        K8sFormat.Ago(TimeSpan.FromSeconds(4)).Should().Be("just now");
        K8sFormat.Ago(TimeSpan.FromSeconds(5)).Should().Be("5s ago");
        K8sFormat.Ago(TimeSpan.FromMinutes(6)).Should().Be("6m ago");
    }

    [Theory]
    [InlineData(500, "500m")]
    [InlineData(999, "999m")]
    [InlineData(1000, "1")]
    [InlineData(1500, "1.5")]
    [InlineData(58600, "58.6")]
    [InlineData(2000, "2")]
    public void Cpu_formats(long milli, string expected) => K8sFormat.Cpu(milli).Should().Be(expected);

    [Theory]
    [InlineData(0, "0")]
    [InlineData(5, "0.01")]
    [InlineData(50, "0.05")]
    [InlineData(99, "0.1")]
    [InlineData(100, "0.1")]
    [InlineData(250, "0.3")]
    [InlineData(1000, "1")]
    [InlineData(1500, "1.5")]
    [InlineData(2000, "2")]
    [InlineData(58600, "58.6")]
    [InlineData(96000, "96")]
    public void Cores_formats_in_cores_at_every_size(long milli, string expected) => K8sFormat.Cores(milli).Should().Be(expected);

    [Theory]
    [InlineData(12L, "12 B")]
    [InlineData(900L * 1024, "900 Ki")]
    [InlineData(512L * 1024 * 1024, "512 Mi")]
    [InlineData(1610612736L, "1.5 Gi")]
    [InlineData(28L * 1024 * 1024 * 1024, "28 Gi")]
    [InlineData(119L * 1024 * 1024 * 1024, "119 Gi")]
    public void Bytes_formats(long bytes, string expected) => K8sFormat.Bytes(bytes).Should().Be(expected);

    [Fact]
    public void Percent_rounds() => K8sFormat.Percent(0.613).Should().Be("61%");

    [Fact]
    public void Images_are_dot_joined() =>
        K8sFormat.Images(["nginx:1", "redis:7"]).Should().Be("nginx:1 · redis:7");

    [Theory]
    [InlineData("500m", 500)]
    [InlineData("250m", 250)]
    [InlineData("2", 2000)]
    [InlineData("0.5", 500)]
    [InlineData("58600000n", 59)]
    [InlineData("1500000000n", 1500)]
    [InlineData("250000u", 250)]
    [InlineData("0n", 0)]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    public void ParseCpuMilli_parses(string? q, long expected) => K8sFormat.ParseCpuMilli(q).Should().Be(expected);

    [Theory]
    [InlineData("512Mi", 512L * 1024 * 1024)]
    [InlineData("1Gi", 1073741824L)]
    [InlineData("1.5Gi", 1610612736L)]
    [InlineData("128974848", 128974848L)]
    [InlineData("1G", 1_000_000_000L)]
    [InlineData("500M", 500_000_000L)]
    [InlineData("1e3", 1000L)]
    [InlineData(null, 0L)]
    public void ParseBytes_parses(string? q, long expected) => K8sFormat.ParseBytes(q).Should().Be(expected);

    [Fact]
    public void ParseBytes_throws_on_unknown_suffix() =>
        FluentActions.Invoking(() => K8sFormat.ParseBytes("5Zz")).Should().Throw<FormatException>();
}
