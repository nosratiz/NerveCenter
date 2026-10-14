using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

public class ExitCodeInterpreterTests
{
    private static K8sTermination T(int code, string? reason = null) => new(code, reason, null, null);

    [Fact]
    public void Oom_with_limit() =>
        ExitCodeInterpreter.Explain(T(137, "OOMKilled"), 512L * 1024 * 1024)
            .Should().Be("Killed by the kernel at the memory limit (512 Mi).");

    [Fact]
    public void Oom_without_limit() =>
        ExitCodeInterpreter.Explain(T(137, "OOMKilled"), null)
            .Should().Be("Killed by the kernel: out of memory (OOMKilled).");

    [Theory]
    [InlineData(137, "Killed with SIGKILL (exit 137) — often the liveness probe or a forced stop.")]
    [InlineData(143, "Stopped with SIGTERM (exit 143).")]
    [InlineData(0, "Completed (exit 0).")]
    [InlineData(1, "The application exited with an error (exit 1). Check the last log lines above.")]
    [InlineData(2, "The application exited with an error (exit 2). Check the last log lines above.")]
    [InlineData(126, "The command isn't executable (exit 126).")]
    [InlineData(127, "The command wasn't found (exit 127).")]
    [InlineData(42, "Exited with code 42.")]
    public void Codes(int code, string expected) =>
        ExitCodeInterpreter.Explain(T(code), null).Should().Be(expected);

    [Fact]
    public void Appends_run_time_when_both_times_are_set()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        ExitCodeInterpreter.Explain(new K8sTermination(0, null, start, start.AddSeconds(41)), null)
            .Should().Be("Completed (exit 0). Ran 41s.");
    }
}
