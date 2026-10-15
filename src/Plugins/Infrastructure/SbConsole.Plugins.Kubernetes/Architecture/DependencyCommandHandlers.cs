using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Architecture;

/// <summary><see cref="To"/> is a node id, or an external host/URL typed by the user (normalised to <c>x:{host}</c>).</summary>
public sealed record AddDependencyCommand(ArchEditContext Context, string From, string To, ArchEdgeKind Kind, string? Label);

public sealed record RemoveDependencyCommand(ArchEditContext Context, ArchEdge Edge);

/// <summary>Adds a hand-made edge the detectors missed. Audited <c>k8s.arch.edit</c>; a duplicate is refused, not audited.</summary>
public sealed class AddDependencyCommandHandler(
    IConnectionProvider connections, IAuditScope audit, [FromKeyedServices(KubernetesPlugin.PluginId)] IPluginStore store,
    TimeProvider clock, ILogger<AddDependencyCommandHandler> logger) : IScopedService
{
    public const int MaxManualEdges = 500;
    public const int MaxLabelLength = 60;

    public Task<PluginResult<ArchEdge>> HandleAsync(AddDependencyCommand cmd, CancellationToken ct = default)
    {
        var to = Target(cmd.To);
        var label = string.IsNullOrWhiteSpace(cmd.Label) ? null : cmd.Label.Trim();
        string? invalid = string.IsNullOrWhiteSpace(cmd.From) ? "Pick the service that depends on something."
            : to is null ? "Pick a node, or type a host such as api.stripe.com."
            : to == cmd.From ? "A service can't depend on itself."
            : label is { Length: > MaxLabelLength } ? $"A label is at most {MaxLabelLength.ToString(CultureInfo.InvariantCulture)} characters."
            : null;
        if (invalid is not null)
        {
            return Task.FromResult(PluginResult<ArchEdge>.Fail(invalid));
        }

        var id = ArchEdge.MakeId(cmd.From, to!);
        var edge = new ArchEdge(id, cmd.From, to!, cmd.Kind, label ?? ArchitectureBuilder.DefaultLabel(cmd.Kind), "manual", true);
        var target = $"{ArchitectureEdits.NodeLabel(cmd.From)} → {ArchitectureEdits.NodeLabel(to!)}";
        return ArchitectureEdits.RunAsync(connections, audit, logger, cmd.Context, target,
            () => ArchitectureStore.UpdateOverlayAsync(store, cmd.Context.ConnectionId, doc =>
            {
                if (doc.Manual.Any(m => ArchEdge.MakeId(m.From, m.To) == id))
                {
                    throw new ArchEditException("That dependency is already on the map.");
                }

                if (doc.Manual.Count >= MaxManualEdges)
                {
                    throw new ArchEditException($"A map holds at most {MaxManualEdges.ToString(CultureInfo.InvariantCulture)} manual dependencies.");
                }

                var manual = doc.Manual.Append(new StoredManualEdge(cmd.From, to!, cmd.Kind, label, cmd.Context.Author, clock.GetUtcNow())).ToList();
                return (doc with { Manual = manual }, (edge, $"added manual {edge.Kind.ToString().ToLowerInvariant()} dependency ({edge.Label})"));
            }, ct), ct);
    }

    /// <summary>A node id passes through; "https://api.stripe.com/v1" or "api.stripe.com:443" becomes "x:api.stripe.com".</summary>
    internal static string? Target(string? input)
    {
        var value = (input ?? "").Trim();
        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (value.Length > 2 && value[1] == ':' && value[0] is 'w' or 'i' or 's' or 'x')
        {
            return value[0] == 'x' ? Target(value[2..]) : value;
        }

        var endpoint = ArchitectureBuilder.Endpoints(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value);
        return endpoint.Count == 1 ? "x:" + endpoint[0].Host : null;
    }
}

/// <summary>
/// Removes a manual edge, or dismisses a detected one (it stays hidden until restored). Audited <c>k8s.arch.edit</c>
/// with where the edge came from ("dismissed detected dependency (env REDIS_URL)").
/// </summary>
public sealed class RemoveDependencyCommandHandler(
    IConnectionProvider connections, IAuditScope audit, [FromKeyedServices(KubernetesPlugin.PluginId)] IPluginStore store,
    ILogger<RemoveDependencyCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(RemoveDependencyCommand cmd, CancellationToken ct = default) =>
        Wrap(ArchitectureEdits.RunAsync(connections, audit, logger, cmd.Context,
            $"{ArchitectureEdits.NodeLabel(cmd.Edge.From)} → {ArchitectureEdits.NodeLabel(cmd.Edge.To)}",
            () => ArchitectureStore.UpdateOverlayAsync(store, cmd.Context.ConnectionId, doc =>
            {
                if (cmd.Edge.Manual)
                {
                    var manual = doc.Manual.Where(m => ArchEdge.MakeId(m.From, m.To) != cmd.Edge.Id).ToList();
                    if (manual.Count == doc.Manual.Count)
                    {
                        throw new ArchEditException("That dependency was already removed — reload to see the map.");
                    }

                    return (doc with { Manual = manual }, (true, "removed manual dependency"));
                }

                if (doc.Dismissed.Contains(cmd.Edge.Id, StringComparer.Ordinal))
                {
                    throw new ArchEditException(ArchitectureEdits.NothingChanged);
                }

                return (doc with { Dismissed = doc.Dismissed.Append(cmd.Edge.Id).ToList() },
                    (true, $"dismissed detected dependency ({cmd.Edge.Source})"));
            }, ct), ct));

    internal static async Task<PluginResult> Wrap(Task<PluginResult<bool>> run)
    {
        var r = await run;
        return r.IsSuccess ? PluginResult.Ok() : PluginResult.Fail(r.Error!);
    }
}

/// <summary>Brings back every dismissed detected edge. Audited <c>k8s.arch.edit</c>; with none dismissed it is refused, not audited.</summary>
public sealed class RestoreDismissedCommandHandler(
    IConnectionProvider connections, IAuditScope audit, [FromKeyedServices(KubernetesPlugin.PluginId)] IPluginStore store,
    ILogger<RestoreDismissedCommandHandler> logger) : IScopedService
{
    public Task<PluginResult> HandleAsync(ArchEditContext context, CancellationToken ct = default) =>
        RemoveDependencyCommandHandler.Wrap(ArchitectureEdits.RunAsync(connections, audit, logger, context, "dismissed dependencies",
            () => ArchitectureStore.UpdateOverlayAsync(store, context.ConnectionId, doc =>
            {
                if (doc.Dismissed.Count == 0)
                {
                    throw new ArchEditException(ArchitectureEdits.NothingChanged);
                }

                var n = doc.Dismissed.Count;
                return (doc with { Dismissed = [] }, (true, $"restored {n.ToString(CultureInfo.InvariantCulture)} dismissed dependenc{(n == 1 ? "y" : "ies")}"));
            }, ct), ct));
}
