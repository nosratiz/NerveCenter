namespace SbConsole.Plugins.Kubernetes.Tests;

internal static class TestData
{
    /// <summary>
    /// A one-cluster kubeconfig. <paramref name="user"/> is the YAML fragment under <c>user:</c>
    /// (indented by the caller's own newlines; default is a static token).
    /// </summary>
    public static string Kubeconfig(string? user = null, string server = "https://k.example:6443", bool skipTls = false, string context = "dev")
    {
        var userBlock = string.Join("\n", (user ?? "token: abc").Split('\n').Select(l => "      " + l));
        return $"""
            apiVersion: v1
            kind: Config
            current-context: {context}
            clusters:
            - name: c1
              cluster:
                server: {server}
                insecure-skip-tls-verify: {(skipTls ? "true" : "false")}
            contexts:
            - name: {context}
              context:
                cluster: c1
                user: u1
            users:
            - name: u1
              user:
            {userBlock}
            """;
    }
}
