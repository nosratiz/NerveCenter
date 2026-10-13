using System.Text;
using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

public class CsvExportTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    public void Fields_are_quoted_per_rfc_4180(string? value, string expected) =>
        CsvFormat.Field(value).Should().Be(expected);

    [Fact]
    public async Task Export_writes_a_header_then_rows_with_null_and_blob_handling()
    {
        using var db = new TempDatabase("""
            CREATE TABLE "my table"(id INTEGER PRIMARY KEY, name TEXT, data BLOB);
            INSERT INTO "my table" VALUES (1, 'a,b', x'0102'), (2, NULL, NULL);
            """);

        var export = await db.Operations().ExportCsvAsync(db.Secret(), "my table", 100);

        export.FileName.Should().Be("my_table.csv");
        export.RowCount.Should().Be(2);
        export.Truncated.Should().BeFalse();
        Encoding.UTF8.GetString(export.Content).Should().Be("id,name,data\r\n1,\"a,b\",AQI=\r\n2,,\r\n");
    }

    [Fact]
    public async Task Export_stops_at_the_cap_and_says_so()
    {
        using var db = new TempDatabase("CREATE TABLE t(x); INSERT INTO t VALUES (1), (2), (3);");

        var export = await db.Operations().ExportCsvAsync(db.Secret(), "t", 2);

        export.RowCount.Should().Be(2);
        export.Truncated.Should().BeTrue();
    }
}
