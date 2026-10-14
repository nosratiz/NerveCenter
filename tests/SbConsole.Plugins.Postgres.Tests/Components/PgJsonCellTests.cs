using Bunit;
using FluentAssertions;
using MudBlazor.Services;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Components;

namespace SbConsole.Plugins.Postgres.Tests.Components;

public class PgJsonCellTests : BunitContext
{
    public PgJsonCellTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private string Render(PgCell cell) =>
        Render<PgJsonCell>(p => p.Add(c => c.Cell, cell)).Find("button").TextContent.Trim();

    [Theory]
    [InlineData("[…] 0 items", 0)]
    [InlineData("[…] 1 item", 1)]
    [InlineData("[…] 2 items", 2)]
    [InlineData("{…} 1 key", 1)]
    [InlineData("{…} 0 keys", 0)]
    public void The_label_is_the_cell_s_own_display(string display, int count) =>
        Render(new PgCell(PgCellKind.Json, display, count, "x")).Should().Be(display);
}
