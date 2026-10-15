using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Client;

namespace SbConsole.Plugins.Kubernetes.Tests.Client;

/// <summary>Spec §9.2: the console's whole knowledge of a Secret is this record, and it has nowhere to hold a value.</summary>
public class SecretSummaryShapeTests
{
    [Fact]
    public void SecretSummary_has_no_value_bearing_members()
    {
        var properties = typeof(SecretSummary).GetProperties().Select(p => p.Name);

        properties.Should().BeEquivalentTo("Namespace", "Name", "Type", "KeyNames", "CreatedAt", "TlsNotAfter");
        typeof(SecretSummary).GetProperty(nameof(SecretSummary.KeyNames))!.PropertyType.Should().Be(typeof(IReadOnlyList<string>));
        typeof(SecretSummary).GetFields().Should().BeEmpty();
    }
}
