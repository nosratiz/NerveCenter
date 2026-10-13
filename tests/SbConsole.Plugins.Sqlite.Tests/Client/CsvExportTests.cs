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
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData("", "")]
    [InlineData("\"", "\"\"\"\"")]
    public void Fields_are_quoted_per_rfc_4180(string? value, string expected) =>
        CsvFormat.Field(value).Should().Be(expected);

    [Fact]
    public async Task Export_writes_a_header_then_rows_with_null_and_blob_handling()
    {
        using var db = new TempDatabase("""
            CREATE TABLE "my table"(id INTEGER PRIMARY KEY, name TEXT, data BLOB);
            INSERT INTO "my table" VALUES (1, 'a,b', x'0102'), (2, NULL, NULL);
            """);

        var export = await db.Operations().ExportCsvAsync(db.Secret(), "my table", 100, CsvExport.MaxBytes);

        export.FileName.Should().Be("my_table.csv");
        export.RowCount.Should().Be(2);
        export.Truncated.Should().BeFalse();
        Encoding.UTF8.GetString(export.Content).Should().Be("id,name,data\r\n1,\"a,b\",AQI=\r\n2,,\r\n");
    }

    [Fact]
    public async Task Export_stops_at_the_cap_and_says_so()
    {
        using var db = new TempDatabase("CREATE TABLE t(x); INSERT INTO t VALUES (1), (2), (3);");

        var export = await db.Operations().ExportCsvAsync(db.Secret(), "t", 2, CsvExport.MaxBytes);

        export.RowCount.Should().Be(2);
        export.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task A_negative_row_cap_exports_the_header_only()
    {
        using var db = new TempDatabase("CREATE TABLE t(x); INSERT INTO t VALUES (1), (2);");

        var export = await db.Operations().ExportCsvAsync(db.Secret(), "t", -1, CsvExport.MaxBytes);

        export.RowCount.Should().Be(0);
        Encoding.UTF8.GetString(export.Content).Should().Be("x\r\n");
    }

    [Fact]
    public async Task Real_values_export_raw_and_culture_invariant()
    {
        using var db = new TempDatabase("CREATE TABLE t(r REAL); INSERT INTO t VALUES (0.1), (1.0), (1e300);");

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var export = await db.Operations().ExportCsvAsync(db.Secret(), "t", 10, CsvExport.MaxBytes);
            Encoding.UTF8.GetString(export.Content).Should().Be("r\r\n0.1\r\n1\r\n1E+300\r\n");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task A_table_with_exactly_the_cap_is_not_truncated()
    {
        using var db = new TempDatabase("CREATE TABLE t(x); INSERT INTO t VALUES (1), (2);");

        var export = await db.Operations().ExportCsvAsync(db.Secret(), "t", 2, CsvExport.MaxBytes);

        export.RowCount.Should().Be(2);
        export.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_byte_cap_smaller_than_the_table_truncates_and_is_respected()
    {
        using var db = new TempDatabase("CREATE TABLE t(x); INSERT INTO t VALUES ('aaaaaaaaaa'), ('bbbbbbbbbb'), ('cccccccccc');");

        // header "x\r\n" = 3 bytes, each row 12 bytes: a cap of 20 fits one row only.
        var export = await db.Operations().ExportCsvAsync(db.Secret(), "t", 100, 20);

        export.Truncated.Should().BeTrue();
        export.RowCount.Should().Be(1);
        export.Content.Length.Should().BeLessThanOrEqualTo(20);
        Encoding.UTF8.GetString(export.Content).Should().StartWith("x\r\n");
    }
}
