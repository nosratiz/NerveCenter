using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Config;

/// <summary>
/// Replace a ConfigMap's data. <see cref="Original"/> is the data as the editor loaded it (for the audit diff);
/// <see cref="ResourceVersion"/> the version it was loaded at (optimistic concurrency).
/// </summary>
public sealed record UpdateConfigMapCommand(
    K8sTarget Target, IReadOnlyDictionary<string, string> Original, IReadOnlyDictionary<string, string> Data, string ResourceVersion);

/// <summary>
/// Mutating; audits <c>k8s.configmap.update</c> with a detail naming the added, changed and removed keys — never a value.
/// No change fails with "Nothing changed." and is not audited. Confirmation is the caller's job (a plain two-button one).
/// </summary>
public sealed class UpdateConfigMapCommandHandler(
    IKubernetesOperations operations, IConnectionProvider connections, IAuditScope audit,
    ILogger<UpdateConfigMapCommandHandler> logger) : IScopedService
{
    public const string Action = "k8s.configmap.update";
    public const string NothingChanged = "Nothing changed.";

    public Task<PluginResult> HandleAsync(UpdateConfigMapCommand cmd, CancellationToken ct = default)
    {
        var t = cmd.Target;
        if (t.Namespace is not { Length: > 0 } ns)
        {
            return Task.FromResult(PluginResult.Fail("A ConfigMap needs a namespace."));
        }

        if (Detail(cmd.Original, cmd.Data) is not { } detail)
        {
            return Task.FromResult(PluginResult.Fail(NothingChanged));
        }

        return CommandRunner.RunAsync(connections, audit, logger, t.ConnectionId, Action, t.AuditTarget, ActionRisk.Mutating,
            async secret =>
            {
                await operations.UpdateConfigMapAsync(secret, ns, t.Name, cmd.Data, cmd.ResourceVersion, ct);
                return detail;
            }, ct);
    }

    /// <summary>"added a, b · changed c · removed d" (key names only, each list sorted); null when nothing differs.</summary>
    internal static string? Detail(IReadOnlyDictionary<string, string> original, IReadOnlyDictionary<string, string> data)
    {
        var added = data.Keys.Where(k => !original.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var changed = data.Where(kv => original.TryGetValue(kv.Key, out var old) && old != kv.Value)
            .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        var removed = original.Keys.Where(k => !data.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();

        var parts = new List<string>(3);
        if (added.Count > 0) parts.Add("added " + string.Join(", ", added));
        if (changed.Count > 0) parts.Add("changed " + string.Join(", ", changed));
        if (removed.Count > 0) parts.Add("removed " + string.Join(", ", removed));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
