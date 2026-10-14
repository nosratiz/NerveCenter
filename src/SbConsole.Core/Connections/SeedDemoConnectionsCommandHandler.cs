using System.Text.Json;
using Microsoft.Extensions.Logging;
using SbConsole.Core.Results;
using SbConsole.Core.Settings;
using SbConsole.Sdk;

namespace SbConsole.Core.Connections;

/// <summary>How many demo entries were created vs. skipped (already existing, unknown kind, or malformed).</summary>
public sealed record DemoSeedResult(int Created, int Skipped);

/// <summary>
/// Opt-in startup seeding of demo connections from the JSON file named by SBC_DEMO_CONNECTIONS
/// (docs/design.md §16). Create-only: an entry whose name already exists is left alone, even if it
/// differs; nothing is ever updated or deleted, so it is idempotent across restarts. Every create goes
/// through <see cref="CreateConnectionCommandHandler"/> -- the same path as the Connections page -- so
/// the secret is encrypted, the plugin's summary is computed and a "connection.create" audit row is
/// written with actor "system". Never throws: demo data must not stop the app from starting.
/// </summary>
public sealed class SeedDemoConnectionsCommandHandler(
    BootstrapOptions options,
    CreateConnectionCommandHandler create,
    IEnumerable<IPlugin> plugins,
    ILogger<SeedDemoConnectionsCommandHandler> logger) : IScopedService
{
    public const string Actor = "system";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task<DemoSeedResult> HandleAsync(CancellationToken ct = default)
    {
        if (options.DemoConnectionsFile is not { } file)
        {
            return new DemoSeedResult(0, 0);
        }

        List<DemoConnectionEntry?>? entries;
        try
        {
            await using var stream = File.OpenRead(file);
            entries = await JsonSerializer.DeserializeAsync<List<DemoConnectionEntry?>>(stream, JsonOptions, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger.LogError(ex, "Demo connections file {File} (SBC_DEMO_CONNECTIONS) could not be read; no demo connections created.", file);
            return new DemoSeedResult(0, 0);
        }

        var kinds = plugins.Select(p => p.ConnectionKind).ToHashSet(StringComparer.Ordinal);
        int created = 0, skipped = 0;
        foreach (var entry in entries ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Kind)
                || string.IsNullOrWhiteSpace(entry.Secret))
            {
                logger.LogWarning("Skipping a demo connection entry in {File}: name, kind and secret are all required.", file);
                skipped++;
                continue;
            }

            if (!kinds.Contains(entry.Kind))
            {
                logger.LogWarning("Skipping demo connection {Name}: no plugin handles connection kind '{Kind}'.", entry.Name, entry.Kind);
                skipped++;
                continue;
            }

            try
            {
                var tags = (entry.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
                var result = await create.HandleAsync(
                    new CreateConnectionCommand(entry.Name.Trim(), entry.Kind, entry.Secret, tags, Actor), ct);
                if (result.IsSuccess)
                {
                    logger.LogInformation("Created demo connection {Name} ({Kind}).", entry.Name, entry.Kind);
                    created++;
                }
                else if (result.Error!.Category == ErrorCategory.Conflict)
                {
                    logger.LogInformation("Demo connection {Name} already exists; left unchanged.", entry.Name);
                    skipped++;
                }
                else
                {
                    logger.LogWarning("Demo connection {Name} was not created: {Error}", entry.Name, result.Error.Message);
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Creating demo connection {Name} failed.", entry.Name);
                skipped++;
            }
        }

        logger.LogInformation("Demo connections from {File}: {Created} created, {Skipped} skipped.", file, created, skipped);
        return new DemoSeedResult(created, skipped);
    }

    private sealed record DemoConnectionEntry(string? Name, string? Kind, string? Secret, IReadOnlyList<string>? Tags);
}
