using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>Spec §13's input rules, which the dialog also uses for its field-level hints.</summary>
public class NewDatabaseRulesTests
{
    [Theory]
    [InlineData("reports", "sqlite-reports")]
    [InlineData("reports.db", "sqlite-reports")]
    [InlineData("Team Tasks.sqlite3", "sqlite-Team Tasks")]
    [InlineData("v1.2", "sqlite-v1.2")]
    [InlineData("  ", "")]
    public void Default_connection_name_is_sqlite_dash_the_base_name(string fileName, string expected) =>
        NewDatabaseRules.DefaultConnectionName(fileName).Should().Be(expected);

    [Theory]
    [InlineData("reports", null)]
    [InlineData("a b-c_d.db", null)]
    [InlineData("", "Type a file name.")]
    [InlineData(".x", "A file name can't start with a dot.")]
    public void File_name_errors(string fileName, string? expected) =>
        NewDatabaseRules.FileNameError(fileName).Should().Be(expected);

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("team", true)]
    [InlineData("team/2026", true)]
    [InlineData("team/../x", false)]
    [InlineData("/abs", false)]
    public void Sub_folder_rules(string? subFolder, bool ok) =>
        (NewDatabaseRules.SubFolderError(subFolder) is null).Should().Be(ok);
}
