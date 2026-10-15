namespace SbConsole.Plugins.Kubernetes.Components;

/// <summary>Where the plugin's ES module lives; components import it lazily via IJSRuntime "import".</summary>
public static class K8sJs
{
    public const string ModulePath = "./_content/SbConsole.Plugins.Kubernetes/kubernetes.js";
}
