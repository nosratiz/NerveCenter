namespace SbConsole.Plugins.Kubernetes.Client;

public static class ExitCodeInterpreter
{
    public static string Explain(K8sTermination t, long? memoryLimitBytes)
    {
        var text = t.ExitCode switch
        {
            137 when t.Reason == "OOMKilled" => memoryLimitBytes is { } limit
                ? $"Killed by the kernel at the memory limit ({K8sFormat.Bytes(limit)})."
                : "Killed by the kernel: out of memory (OOMKilled).",
            137 => "Killed with SIGKILL (exit 137) — often the liveness probe or a forced stop.",
            143 => "Stopped with SIGTERM (exit 143).",
            0 => "Completed (exit 0).",
            1 or 2 => $"The application exited with an error (exit {t.ExitCode}). Check the last log lines above.",
            126 => "The command isn't executable (exit 126).",
            127 => "The command wasn't found (exit 127).",
            _ => $"Exited with code {t.ExitCode}.",
        };
        return t.StartedAt is { } s && t.FinishedAt is { } f ? $"{text} Ran {K8sFormat.Age(f - s)}." : text;
    }
}
