using System.Diagnostics;

namespace SbConsole.Plugins.Kubernetes.Tests.Infrastructure;

/// <summary>A fact that is skipped when SBC_SKIP_DOCKER_TESTS=1 or Docker isn't reachable.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = "Docker not available";
        }
    }
}

internal static class DockerAvailability
{
    private static readonly Lazy<bool> Lazy = new(Probe);

    public static bool IsAvailable => Lazy.Value;

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("SBC_SKIP_DOCKER_TESTS") == "1")
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return false;
            }

            // Drain so a full pipe can't block the exit.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
