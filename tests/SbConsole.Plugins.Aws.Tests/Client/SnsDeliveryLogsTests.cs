using ResourceNotFoundException = Amazon.CloudWatchLogs.Model.ResourceNotFoundException;
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class SnsDeliveryLogsTests
{
    private const string SuccessGroup = "sns/us-east-1/123456789012/orders";
    private const string FailureGroup = "sns/us-east-1/123456789012/orders/Failure";

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    // --- log group names ---------------------------------------------------------------------

    [Fact]
    public void Log_group_names_come_from_the_topic_arn_region_account_and_name()
    {
        var (success, failure) = SnsDeliveryLogs.LogGroupNames("arn:aws:sns:eu-west-1:111122223333:shipment-updates.fifo");

        success.Should().Be("sns/eu-west-1/111122223333/shipment-updates.fifo");
        failure.Should().Be("sns/eu-west-1/111122223333/shipment-updates.fifo/Failure");
    }

    [Theory]
    [InlineData("not-an-arn")]
    [InlineData("arn:aws:sns:us-east-1:123")]
    [InlineData("arn:aws:sqs:us-east-1:123:queue")]
    public void A_malformed_topic_arn_is_rejected(string arn)
    {
        var act = () => SnsDeliveryLogs.LogGroupNames(arn);

        act.Should().Throw<ArgumentException>();
    }

    // --- parser ------------------------------------------------------------------------------

    private const string SqsSuccessJson = """
        {"notification":{"messageMD5Sum":"abc","messageId":"m-1","topicArn":"arn:aws:sns:us-east-1:123456789012:orders","timestamp":"2026-09-25 11:59:58.100"},
         "delivery":{"deliveryId":"d-1","destination":"arn:aws:sqs:us-east-1:123456789012:orders-queue","providerResponse":"{\"sqsRequestId\":\"r\"}","dwellTimeMs":23,"attempts":1,"statusCode":200},
         "status":"SUCCESS"}
        """;

    [Fact]
    public void Parses_every_field_of_a_delivery_status_event()
    {
        var timestampMs = Now.ToUnixTimeMilliseconds();

        var entry = SnsDeliveryLogs.ParseEvent(timestampMs, SqsSuccessJson, fromFailureGroup: false);

        entry.Timestamp.Should().Be(Now);
        entry.Status.Should().Be("SUCCESS");
        entry.MessageId.Should().Be("m-1");
        entry.Destination.Should().Be("arn:aws:sqs:us-east-1:123456789012:orders-queue");
        entry.StatusCode.Should().Be(200);
        entry.ProviderResponse.Should().Be("{\"sqsRequestId\":\"r\"}");
        entry.DwellTimeMs.Should().Be(23);
        entry.Attempts.Should().Be(1);
        entry.RawMessage.Should().BeNull();
        entry.IsFailure.Should().BeFalse();
    }

    [Fact]
    public void Missing_optional_fields_stay_null_as_in_an_sms_delivery_log()
    {
        const string sms = """
            {"notification":{"messageId":"m-2","timestamp":"2016-06-28 00:40:34.559"},
             "delivery":{"destination":"+1XXX5550100","providerResponse":"Unknown error attempting to reach phone","dwellTimeMs":1420},
             "status":"FAILURE"}
            """;

        var entry = SnsDeliveryLogs.ParseEvent(0, sms, fromFailureGroup: true);

        entry.Status.Should().Be("FAILURE");
        entry.IsFailure.Should().BeTrue();
        entry.StatusCode.Should().BeNull();
        entry.Attempts.Should().BeNull();
        entry.DwellTimeMs.Should().Be(1420);
        entry.ProviderResponse.Should().Be("Unknown error attempting to reach phone");
    }

    [Theory]
    [InlineData("this is not json", false, "SUCCESS")]
    [InlineData("[1,2,3]", true, "FAILURE")]
    [InlineData("{\"status\":", true, "FAILURE")]
    [InlineData("", false, "SUCCESS")]
    public void An_unparseable_event_is_kept_with_its_raw_text_and_a_group_derived_status(string message, bool fromFailureGroup, string expectedStatus)
    {
        var entry = SnsDeliveryLogs.ParseEvent(Now.ToUnixTimeMilliseconds(), message, fromFailureGroup);

        entry.RawMessage.Should().Be(message);
        entry.Status.Should().Be(expectedStatus);
        entry.Timestamp.Should().Be(Now);
        entry.MessageId.Should().BeNull();
    }

    [Fact]
    public void Wrong_typed_fields_are_ignored_rather_than_crashing()
    {
        const string odd = """{"notification":{"messageId":7},"delivery":{"statusCode":"200","attempts":1.5,"dwellTimeMs":"x","providerResponse":{"a":1}}}""";

        var entry = SnsDeliveryLogs.ParseEvent(0, odd, fromFailureGroup: true);

        entry.RawMessage.Should().BeNull();
        entry.MessageId.Should().BeNull();
        entry.StatusCode.Should().BeNull();
        entry.Attempts.Should().BeNull();
        entry.DwellTimeMs.Should().BeNull();
        entry.ProviderResponse.Should().Be("{\"a\":1}");
        entry.Status.Should().Be("FAILURE");
    }

    // --- merge -------------------------------------------------------------------------------

    private static DeliveryLogEntry Entry(int minutesAgo, string status = "SUCCESS") =>
        new(Now.AddMinutes(-minutesAgo), status, $"m-{minutesAgo}", null, null, null, null, null);

    [Fact]
    public void Merge_sorts_newest_first_and_takes_the_limit()
    {
        var merged = SnsDeliveryLogs.MergeNewestFirst([Entry(5), Entry(1, "FAILURE"), Entry(9), Entry(3)], limit: 3);

        merged.Select(e => e.MessageId).Should().Equal("m-1", "m-3", "m-5");
    }

    // --- scan slices -------------------------------------------------------------------------

    [Fact]
    public void Scan_slices_walk_backwards_from_now_without_overlap_and_cover_the_window()
    {
        var slices = SnsDeliveryLogs.BuildScanSlices(Now, TimeSpan.FromDays(7));

        slices[0].End.Should().Be(Now);
        slices[^1].Start.Should().Be(Now - TimeSpan.FromDays(7));
        for (var i = 1; i < slices.Count; i++)
        {
            slices[i].End.Should().Be(slices[i - 1].Start.AddMilliseconds(-1));
            slices[i].Start.Should().BeBefore(slices[i].End);
        }
    }

    [Fact]
    public void A_one_hour_window_is_a_single_slice()
    {
        SnsDeliveryLogs.BuildScanSlices(Now, TimeSpan.FromHours(1)).Should().ContainSingle()
            .Which.Should().Be((Now - TimeSpan.FromHours(1), Now));
    }

    // --- scan orchestration ------------------------------------------------------------------

    private sealed class FakeLogs
    {
        public HashSet<string> MissingGroups { get; } = [];
        public Dictionary<string, List<(long, string)>> Events { get; } = new() { [SuccessGroup] = [], [FailureGroup] = [] };
        public int PageSize { get; set; } = 1000;
        public List<(string Group, DateTimeOffset Start, DateTimeOffset End)> Calls { get; } = [];
        public Exception? Throw { get; set; }

        public Task<DeliveryLogPage> FetchAsync(string group, DateTimeOffset start, DateTimeOffset end, string? token, CancellationToken ct)
        {
            Calls.Add((group, start, end));
            if (Throw is not null)
            {
                throw Throw;
            }

            if (MissingGroups.Contains(group))
            {
                throw new ResourceNotFoundException("The specified log group does not exist.");
            }

            // Ascending, like FilterLogEvents.
            var inRange = Events[group]
                .Where(e => e.Item1 >= start.ToUnixTimeMilliseconds() && e.Item1 <= end.ToUnixTimeMilliseconds())
                .OrderBy(e => e.Item1)
                .ToList();
            var offset = token is null ? 0 : int.Parse(token);
            var page = inRange.Skip(offset).Take(PageSize).ToList();
            var next = offset + PageSize < inRange.Count ? (offset + PageSize).ToString() : null;
            return Task.FromResult(new DeliveryLogPage(page, next));
        }

        public void Add(string group, TimeSpan ago, string status)
        {
            var prefix = group.EndsWith("Failure", StringComparison.Ordinal) ? "f" : "s";
            Events[group].Add(((Now - ago).ToUnixTimeMilliseconds(), $"{{\"notification\":{{\"messageId\":\"{prefix}-{ago.TotalMinutes}\"}},\"status\":\"{status}\"}}"));
        }
    }

    private static Task<DeliveryLogsResult> Scan(FakeLogs fake, TimeSpan window, int limit = 100, int maxPages = 20) =>
        SnsDeliveryLogs.ScanAsync(fake.FetchAsync, SuccessGroup, FailureGroup, SnsDeliveryLogs.BuildScanSlices(Now, window), limit, maxPages, CancellationToken.None);

    [Fact]
    public async Task Both_log_groups_missing_means_logging_is_not_configured()
    {
        var fake = new FakeLogs();
        fake.MissingGroups.UnionWith([SuccessGroup, FailureGroup]);

        var result = await Scan(fake, TimeSpan.FromDays(7));

        result.LoggingNotConfigured.Should().BeTrue();
        result.Entries.Should().BeEmpty();
        // Stops after learning both groups are missing -- no call per slice.
        fake.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Only_the_failure_group_existing_still_returns_its_events()
    {
        var fake = new FakeLogs();
        fake.MissingGroups.Add(SuccessGroup);
        fake.Add(FailureGroup, TimeSpan.FromMinutes(10), "FAILURE");

        var result = await Scan(fake, TimeSpan.FromHours(24));

        result.LoggingNotConfigured.Should().BeFalse();
        result.Entries.Should().ContainSingle().Which.Status.Should().Be("FAILURE");
        fake.Calls.Count(c => c.Group == SuccessGroup).Should().Be(1);
    }

    [Fact]
    public async Task Merges_both_groups_newest_first()
    {
        var fake = new FakeLogs();
        fake.Add(SuccessGroup, TimeSpan.FromMinutes(30), "SUCCESS");
        fake.Add(FailureGroup, TimeSpan.FromMinutes(20), "FAILURE");
        fake.Add(SuccessGroup, TimeSpan.FromMinutes(10), "SUCCESS");
        fake.Add(SuccessGroup, TimeSpan.FromHours(30), "SUCCESS");

        var result = await Scan(fake, TimeSpan.FromDays(7));

        result.Entries.Select(e => e.MessageId).Should().Equal("s-10", "f-20", "s-30", "s-1800");
        result.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task Events_outside_the_window_are_not_returned()
    {
        var fake = new FakeLogs();
        fake.Add(SuccessGroup, TimeSpan.FromMinutes(30), "SUCCESS");
        fake.Add(SuccessGroup, TimeSpan.FromHours(2), "SUCCESS");

        var result = await Scan(fake, TimeSpan.FromHours(1));

        result.Entries.Should().ContainSingle().Which.MessageId.Should().Be("s-30");
    }

    [Fact]
    public async Task Stops_scanning_older_slices_once_the_limit_is_reached_and_reports_more_may_exist()
    {
        var fake = new FakeLogs();
        for (var i = 1; i <= 5; i++)
        {
            fake.Add(SuccessGroup, TimeSpan.FromMinutes(i), "SUCCESS");
        }

        fake.Add(SuccessGroup, TimeSpan.FromDays(3), "SUCCESS");

        var result = await Scan(fake, TimeSpan.FromDays(7), limit: 3);

        result.Entries.Select(e => e.MessageId).Should().Equal("s-1", "s-2", "s-3");
        result.IsTruncated.Should().BeTrue();
        // Only the newest (1h) slice was scanned.
        fake.Calls.Should().OnlyContain(c => c.End == Now);
    }

    [Fact]
    public async Task The_page_budget_bounds_the_scan_and_is_reported_as_truncated()
    {
        var fake = new FakeLogs { PageSize = 1 };
        for (var i = 1; i <= 10; i++)
        {
            fake.Add(SuccessGroup, TimeSpan.FromMinutes(i), "SUCCESS");
        }

        var result = await Scan(fake, TimeSpan.FromHours(1), limit: 100, maxPages: 4);

        fake.Calls.Should().HaveCount(4);
        result.Entries.Should().HaveCount(4);
        result.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Any_other_failure_propagates()
    {
        var fake = new FakeLogs { Throw = new InvalidOperationException("boom") };

        var act = () => Scan(fake, TimeSpan.FromHours(1));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
