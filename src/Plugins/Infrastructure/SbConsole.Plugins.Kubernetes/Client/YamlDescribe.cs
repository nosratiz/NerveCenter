// Allowed exception to the "only KubernetesOperations*/ClientFactory/K8sMappers touch k8s.*" rule: this file serializes k8s model objects.
using k8s;
using k8s.Models;

namespace SbConsole.Plugins.Kubernetes.Client;

/// <summary>YAML for the "View YAML" dialog: the object as the API returned it, minus <c>metadata.managedFields</c>.</summary>
public static class YamlDescribe
{
    /// <summary>
    /// Serializes a freshly-read object (it is mutated: managedFields is nulled). Secrets are refused outright as
    /// defence in depth — nothing in the plugin should ever hand one in.
    /// </summary>
    public static string Serialize(object k8sObject)
    {
        ArgumentNullException.ThrowIfNull(k8sObject);
        if (k8sObject is V1Secret)
        {
            throw new InvalidOperationException("Secrets are never serialized.");
        }

        if (k8sObject is IMetadata<V1ObjectMeta> { Metadata: { } metadata })
        {
            metadata.ManagedFields = null;
        }

        return KubernetesYaml.Serialize(k8sObject);
    }
}
