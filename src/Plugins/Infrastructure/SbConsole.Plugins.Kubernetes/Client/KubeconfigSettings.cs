using System.Text.Json;

namespace SbConsole.Plugins.Kubernetes.Client;

/// <summary>The connection secret: a verbatim kubeconfig plus the context to use, as JSON.</summary>
public sealed record KubeconfigSettings(string Kubeconfig, string Context)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Derived from the current values on each read, so a <c>with</c> copy is never stale. Throws FormatException if invalid.</summary>
    public KubeconfigSummary Summary =>
        KubeconfigValidator.Validate(Kubeconfig, Context).Summary
        ?? throw new FormatException(string.Join("; ", KubeconfigValidator.Validate(Kubeconfig, Context).Errors));

    public static KubeconfigSettings Parse(string secret)
    {
        KubeconfigSettings settings;
        try
        {
            using var doc = JsonDocument.Parse(secret);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kubeconfig", out var kc) || kc.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("context", out var cx) || cx.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("The connection secret must contain 'kubeconfig' and 'context'.");
            }

            settings = new KubeconfigSettings(kc.GetString()!, cx.GetString()!);
        }
        catch (JsonException)
        {
            throw new FormatException("The connection secret isn't valid JSON.");
        }

        var validation = KubeconfigValidator.Validate(settings.Kubeconfig, settings.Context);
        if (validation.Errors.Count > 0)
        {
            throw new FormatException(string.Join("; ", validation.Errors));
        }

        // An empty stored context means "current-context"; pin it so the stored value always matches the summary.
        return string.IsNullOrEmpty(settings.Context) ? settings with { Context = validation.Summary!.Context } : settings;
    }

    public string ToSecretString() =>
        JsonSerializer.Serialize(new { kubeconfig = Kubeconfig, context = Context }, Json);
}
