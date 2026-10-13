using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Tables;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Tables;

public class RowsQueryHandlerTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    public RowsQueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    [Fact]
    public async Task Samples_use_the_default_sample_size()
    {
        IReadOnlyList<ColumnSample> samples = [new ColumnSample("ts", 1000, 1000, 1000, 0, 0, 1000, 1000)];
        _operations.SampleColumnsAsync(Secret, "events", ColumnSample.DefaultSampleRows, Arg.Any<CancellationToken>()).Returns(samples);

        var result = await new GetColumnSamplesQueryHandler(_operations, _connections, NullLogger<GetColumnSamplesQueryHandler>.Instance)
            .HandleAsync(_connectionId, "events");

        result.Value.Should().BeSameAs(samples);
    }

    [Fact]
    public async Task Preview_uses_the_preview_limit()
    {
        var page = new RowPage("SELECT 1", ["x"], []);
        _operations.PreviewRowsAsync(Secret, "events", RowPage.PreviewLimit, Arg.Any<CancellationToken>()).Returns(page);

        var result = await new PreviewRowsQueryHandler(_operations, _connections, NullLogger<PreviewRowsQueryHandler>.Instance)
            .HandleAsync(_connectionId, "events");

        result.Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task Count_returns_the_exact_count()
    {
        _operations.CountRowsAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns(41_204_117L);

        var result = await new CountRowsQueryHandler(_operations, _connections, NullLogger<CountRowsQueryHandler>.Instance)
            .HandleAsync(_connectionId, "events");

        result.Value.Should().Be(41_204_117L);
    }

    [Fact]
    public async Task A_cancelled_count_rethrows_instead_of_reporting_an_error()
    {
        using var cts = new CancellationTokenSource();
        _operations.CountRowsAsync(Secret, "events", Arg.Any<CancellationToken>()).Returns<Task<long>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => new CountRowsQueryHandler(_operations, _connections, NullLogger<CountRowsQueryHandler>.Instance)
            .HandleAsync(_connectionId, "events", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
