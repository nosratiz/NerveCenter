using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Architecture;

/// <summary>
/// <see cref="ExpectedVersion"/> is the version the editor opened (0 = none yet). Blank <see cref="Text"/> clears the description.
/// </summary>
public sealed record SaveDescriptionCommand(ArchEditContext Context, string NodeId, string Text, int ExpectedVersion);

/// <summary>
/// Saves a node's description as console data, versioned, audited <c>k8s.arch.edit</c> ("description v4" / "description cleared"),
/// never the text. A save against an older version than the stored one is refused. Returns the stored description (null when cleared).
/// </summary>
public sealed class SaveDescriptionCommandHandler(
    IConnectionProvider connections, IAuditScope audit, [FromKeyedServices(KubernetesPlugin.PluginId)] IPluginStore store,
    TimeProvider clock, ILogger<SaveDescriptionCommandHandler> logger) : IScopedService
{
    public const int MaxLength = 4000;

    public Task<PluginResult<ArchDescription?>> HandleAsync(SaveDescriptionCommand cmd, CancellationToken ct = default)
    {
        var text = (cmd.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cmd.NodeId))
        {
            return Task.FromResult(PluginResult<ArchDescription?>.Fail("No node selected."));
        }

        if (text.Length > MaxLength)
        {
            return Task.FromResult(PluginResult<ArchDescription?>.Fail(
                $"A description is at most {MaxLength.ToString(CultureInfo.InvariantCulture)} characters."));
        }

        return ArchitectureEdits.RunAsync<ArchDescription?>(connections, audit, logger, cmd.Context, ArchitectureEdits.NodeLabel(cmd.NodeId),
            () => ArchitectureStore.UpdateDescriptionsAsync(store, cmd.Context.ConnectionId, doc =>
            {
                var current = doc.GetValueOrDefault(cmd.NodeId);
                var currentVersion = current?.Version ?? 0;
                if (currentVersion != cmd.ExpectedVersion)
                {
                    throw new ArchEditException(current is null
                        ? "This description was removed since you opened it — reload to see it."
                        : $"{current.Author} saved v{currentVersion.ToString(CultureInfo.InvariantCulture)} since you opened it — reload to see it.");
                }

                if ((current?.Text ?? "") == text)
                {
                    throw new ArchEditException(ArchitectureEdits.NothingChanged);
                }

                if (text.Length == 0)
                {
                    doc.Remove(cmd.NodeId);
                    return (doc, ((ArchDescription?)null, "description cleared"));
                }

                var saved = new ArchDescription(text, currentVersion + 1, cmd.Context.Author, clock.GetUtcNow());
                doc[cmd.NodeId] = saved;
                return (doc, ((ArchDescription?)saved, $"description v{saved.Version.ToString(CultureInfo.InvariantCulture)}"));
            }, ct), ct);
    }
}
