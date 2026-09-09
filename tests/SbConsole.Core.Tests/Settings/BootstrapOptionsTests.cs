using System.Security.Cryptography;
using FluentAssertions;
using SbConsole.Core.Settings;

namespace SbConsole.Core.Tests.Settings;

public class BootstrapOptionsTests
{
    private static readonly string ValidKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static Dictionary<string, string?> ValidEnv() => new()
    {
        ["SBC_DB_PATH"] = "/data/sbc.db",
        ["SBC_DATA_KEY"] = ValidKey,
        ["SBC_ADMIN_PASSWORD"] = "hunter2",
        ["SBC_API_KEY"] = "api-key",
        ["SBC_BIND"] = "http://0.0.0.0:8080",
    };

    [Fact]
    public void Reads_all_values_from_environment()
    {
        var env = ValidEnv();

        var options = BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        options.DbPath.Should().Be("/data/sbc.db");
        options.DataKey.Should().HaveCount(32);
        options.AdminPassword.Should().Be("hunter2");
        options.ApiKey.Should().Be("api-key");
        options.Bind.Should().Be("http://0.0.0.0:8080");
    }

    [Fact]
    public void Bind_defaults_when_missing()
    {
        var env = ValidEnv();
        env["SBC_BIND"] = null;

        BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k)).Bind
            .Should().Be("http://localhost:5080");
    }

    [Theory]
    [InlineData("SBC_DB_PATH")]
    [InlineData("SBC_DATA_KEY")]
    [InlineData("SBC_ADMIN_PASSWORD")]
    [InlineData("SBC_API_KEY")]
    public void Missing_required_variable_throws_naming_it(string missing)
    {
        var env = ValidEnv();
        env[missing] = null;

        var act = () => BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
    }

    [Fact]
    public void Data_key_must_be_32_bytes()
    {
        var env = ValidEnv();
        env["SBC_DATA_KEY"] = Convert.ToBase64String(new byte[16]);

        var act = () => BootstrapOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        act.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }
}
