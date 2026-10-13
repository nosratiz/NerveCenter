using FluentAssertions;
using SbConsole.Core.Security;
using SbConsole.Core.Settings;

namespace SbConsole.Core.Tests.Security;

public sealed class HostPathPolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbc-paths-").FullName;

    public HostPathPolicyTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(_dir, "data2"));
        Directory.CreateDirectory(Path.Combine(_dir, "secret"));
    }

    private string Root => Path.Combine(_dir, "data");

    private HostPathPolicy Policy(string? dbPath = null, params string[] roots) =>
        new(new BootstrapOptions(dbPath ?? Path.Combine(_dir, "host", "sbconsole.db"), new byte[32], "pw", "key", "http://localhost")
        {
            SqliteRoots = roots,
        });

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Refuses_everything_when_no_roots_are_configured()
    {
        var file = Touch("data/reports.db");

        var result = Policy().Check(file);

        result.Allowed.Should().BeFalse();
        result.ResolvedPath.Should().BeNull();
        result.Reason.Should().Contain("SBC_SQLITE_ROOTS");
    }

    [Fact]
    public void Allows_a_file_under_a_root_and_returns_its_resolved_path()
    {
        var file = Touch("data/reports.db");

        var result = Policy(null, Root).Check(file);

        result.Allowed.Should().BeTrue();
        result.ResolvedPath.Should().Be(HostPathPolicy.ResolveReal(file));
        result.Reason.Should().BeNull();
    }

    [Fact]
    public void Allows_a_file_under_any_of_several_roots()
    {
        var file = Touch("data2/other.db");

        Policy(null, Root, Path.Combine(_dir, "data2")).Check(file).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_sibling_directory_that_shares_the_roots_prefix()
    {
        var file = Touch("data2/reports.db");

        var result = Policy(null, Root).Check(file);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("outside the allowed SQLite roots");
    }

    [Fact]
    public void Refuses_dot_dot_traversal_out_of_a_root()
    {
        Touch("secret/keys.db");

        Policy(null, Root).Check(Path.Combine(Root, "..", "secret", "keys.db")).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Refuses_a_symlinked_file_that_points_outside_the_root()
    {
        var outside = Touch("secret/keys.db");
        var link = Path.Combine(Root, "innocent.db");
        File.CreateSymbolicLink(link, outside);

        Policy(null, Root).Check(link).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Refuses_a_file_under_a_symlinked_directory_that_points_outside_the_root()
    {
        Touch("secret/keys.db");
        Directory.CreateSymbolicLink(Path.Combine(Root, "sub"), Path.Combine(_dir, "secret"));

        Policy(null, Root).Check(Path.Combine(Root, "sub", "keys.db")).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Allows_a_symlink_that_stays_inside_the_root()
    {
        var target = Touch("data/real.db");
        var link = Path.Combine(Root, "alias.db");
        File.CreateSymbolicLink(link, target);

        var result = Policy(null, Root).Check(link);

        result.Allowed.Should().BeTrue();
        result.ResolvedPath.Should().Be(HostPathPolicy.ResolveReal(target));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public void Refuses_the_hosts_own_database_and_its_sidecar_files_even_under_a_root(string suffix)
    {
        var dbPath = Path.Combine(Root, "sbconsole.db");
        var file = Touch("data/sbconsole.db" + suffix);

        var result = Policy(dbPath, Root).Check(file);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("SbConsole itself");
    }

    [Fact]
    public void Refuses_an_empty_path()
    {
        Policy(null, Root).Check("  ").Allowed.Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp dir cleanup is best effort.
        }
    }
}
