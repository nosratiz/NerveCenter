using System.Text.Json;
using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

/// <summary>
/// docker/sqlite/demo-connections.json is fed to SBC_DEMO_CONNECTIONS by the compose stack (one file
/// for every plugin). The kubernetes entries must parse exactly as a pasted connection secret would,
/// so a validator change can't silently leave the demo file behind.
/// </summary>
public class DemoConnectionsKubernetesTests
{
    private static string DemoFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SbConsole.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return Path.Combine(dir!.FullName, "docker", "sqlite", "demo-connections.json");
    }

    [Fact]
    public void Demo_file_kubernetes_entries_parse()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DemoFile()));
        var entries = doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == "kubernetes")
            .ToList();

        entries.Select(e => e.GetProperty("name").GetString()).Should().Equal("k3s-local", "k3s-local-viewer");

        foreach (var entry in entries)
        {
            var name = entry.GetProperty("name").GetString()!;
            var settings = KubeconfigSettings.Parse(entry.GetProperty("secret").GetString()!);
            settings.Context.Should().Be(name);
            settings.Kubeconfig.Should().Contain("server: https://k3s:6443");
        }

        entries[0].GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("prod");
        entries[1].GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("demo");
    }
}
