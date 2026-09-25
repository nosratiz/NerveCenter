using System.Text.Json;
using Amazon.CloudWatchLogs.Model;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>One FilterLogEvents page, reduced to what the scan needs (event time in epoch ms + text).</summary>
internal sealed record DeliveryLogPage(IReadOnlyList<(long TimestampMs, string Message)> Events, string? NextToken);

/// <summary>Fetches one FilterLogEvents page for a log group and time range (both ends inclusive).</summary>
internal delegate Task<DeliveryLogPage> DeliveryLogPageFetcher(string logGroup, DateTimeOffset start, DateTimeOffset end, string? nextToken, CancellationToken ct);

/// <summary>
/// The pure parts of reading SNS delivery-status logs, kept apart from SnsOperations so they are
/// unit-testable without AWS (the page fetch is injected).
///
/// SNS writes delivery-status logs (when a topic has a &lt;Protocol&gt;SuccessFeedbackRoleArn /
/// &lt;Protocol&gt;FailureFeedbackRoleArn set) to two CloudWatch Logs groups named from the topic ARN:
/// <c>sns/{region}/{accountId}/{topicName}</c> for successes and
/// <c>sns/{region}/{accountId}/{topicName}/Failure</c> for failures. SNS creates each group on its
/// first write, so a missing group means "nothing ever logged there".
/// </summary>
internal static class SnsDeliveryLogs
{
    /// <summary>FilterLogEvents page size (the API allows up to 10,000).</summary>
    internal const int PageSize = 1000;

    /// <summary>Total FilterLogEvents calls per load, across both groups and all slices.</summary>
    internal const int MaxPages = 10;

    // Slice boundaries (distance back from now). FilterLogEvents only pages oldest-first, so a
    // single call over 7 days would reach the newest events last; walking newer slices first lets
    // the scan stop as soon as it has `limit` events without paging through the whole window.
    private static readonly TimeSpan[] SliceEdges = [TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24), TimeSpan.FromDays(7)];

    internal static (string Success, string Failure) LogGroupNames(string topicArn)
    {
        // arn:{partition}:sns:{region}:{account}:{name}
        var parts = topicArn.Split(':');
        if (parts.Length != 6 || parts[0] != "arn" || parts[2] != "sns" || parts.Skip(3).Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException($"Not an SNS topic ARN: {topicArn}", nameof(topicArn));
        }

        var success = $"sns/{parts[3]}/{parts[4]}/{parts[5]}";
        return (success, success + "/Failure");
    }

    /// <summary>Non-overlapping [Start, End] slices covering [now - window, now], newest first.</summary>
    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> BuildScanSlices(DateTimeOffset now, TimeSpan window)
    {
        var edges = SliceEdges.Where(e => e < window).Append(window).ToList();
        var slices = new List<(DateTimeOffset, DateTimeOffset)>(edges.Count);
        var end = now;
        foreach (var edge in edges)
        {
            var start = now - edge;
            slices.Add((start, end));
            end = start.AddMilliseconds(-1);
        }

        return slices;
    }

    /// <summary>
    /// Scans both groups slice by slice (newest slice first), stopping once <paramref name="limit"/>
    /// events are collected or <paramref name="maxPages"/> calls were made. A group that doesn't
    /// exist (ResourceNotFoundException) is skipped from then on; both missing = not configured.
    /// Any other failure propagates.
    /// </summary>
    internal static async Task<DeliveryLogsResult> ScanAsync(
        DeliveryLogPageFetcher fetchPage, string successGroup, string failureGroup,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> slices, int limit, int maxPages, CancellationToken ct)
    {
        var groups = new[] { (Name: successGroup, IsFailure: false), (Name: failureGroup, IsFailure: true) };
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<DeliveryLogEntry>();
        var pages = 0;
        var budgetExhausted = false;
        var slicesScanned = 0;

        foreach (var (start, end) in slices)
        {
            foreach (var (group, isFailure) in groups)
            {
                if (missing.Contains(group))
                {
                    continue;
                }

                string? nextToken = null;
                do
                {
                    if (pages >= maxPages)
                    {
                        budgetExhausted = true;
                        break;
                    }

                    DeliveryLogPage page;
                    try
                    {
                        page = await fetchPage(group, start, end, nextToken, ct);
                    }
                    catch (ResourceNotFoundException)
                    {
                        missing.Add(group);
                        break;
                    }

                    pages++;
                    entries.AddRange(page.Events.Select(e => ParseEvent(e.TimestampMs, e.Message, isFailure)));
                    nextToken = page.NextToken;
                } while (!string.IsNullOrEmpty(nextToken));

                if (budgetExhausted)
                {
                    break;
                }
            }

            if (missing.Count == groups.Length)
            {
                return new DeliveryLogsResult([], LoggingNotConfigured: true, IsTruncated: false);
            }

            slicesScanned++;
            if (budgetExhausted || entries.Count >= limit)
            {
                break;
            }
        }

        // Older slices left unscanned may hold more events once the limit is reached.
        var isTruncated = budgetExhausted || entries.Count > limit || (entries.Count >= limit && slicesScanned < slices.Count);
        return new DeliveryLogsResult(MergeNewestFirst(entries, limit), LoggingNotConfigured: false, isTruncated);
    }

    internal static IReadOnlyList<DeliveryLogEntry> MergeNewestFirst(IEnumerable<DeliveryLogEntry> entries, int limit) =>
        entries.OrderByDescending(e => e.Timestamp).Take(limit).ToList();

    /// <summary>
    /// Parses one delivery-status event. Never throws: text that isn't a JSON object is kept as
    /// <see cref="DeliveryLogEntry.RawMessage"/>, and a field of an unexpected type is left null.
    /// A missing "status" falls back to the group the event came from.
    /// </summary>
    internal static DeliveryLogEntry ParseEvent(long timestampMs, string message, bool fromFailureGroup)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        var groupStatus = fromFailureGroup ? "FAILURE" : "SUCCESS";
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Raw();
            }

            var notification = Child(root, "notification");
            var delivery = Child(root, "delivery");
            return new DeliveryLogEntry(
                timestamp,
                StringField(root, "status") ?? groupStatus,
                StringField(notification, "messageId"),
                StringField(delivery, "destination"),
                IntField(delivery, "statusCode"),
                delivery is { } d && d.TryGetProperty("providerResponse", out var response)
                    ? response.ValueKind == JsonValueKind.String ? response.GetString() : response.GetRawText()
                    : null,
                delivery is { } d2 && d2.TryGetProperty("dwellTimeMs", out var dwell) && dwell.ValueKind == JsonValueKind.Number && dwell.TryGetInt64(out var dwellMs) ? dwellMs : null,
                IntField(delivery, "attempts"));
        }
        catch (JsonException)
        {
            return Raw();
        }

        DeliveryLogEntry Raw() => new(timestamp, groupStatus, null, null, null, null, null, null, RawMessage: message);
    }

    private static JsonElement? Child(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : null;

    private static string? StringField(JsonElement? parent, string name) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntField(JsonElement? parent, string name) =>
        parent is { } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
