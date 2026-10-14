namespace SbConsole.Plugins.Kubernetes.Client;

public sealed record K8sErrorInfo(string Message, int? StatusCode, string? Reason, bool IsUnreachable);
