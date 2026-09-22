using System.Text.Json;

namespace SbConsole.Core.Data.Entities;

public sealed class Connection
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public string TagsCsv { get; set; } = "";
    public required byte[] SecretCiphertext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
    public string? LastTestError { get; set; }
    public string? SummaryJson { get; set; }

    public IReadOnlyList<string> Tags => TagsCsv.Length == 0 ? [] : TagsCsv.Split(',');

    public IReadOnlyDictionary<string, string> Summary => SummaryJson is null
        ? new Dictionary<string, string>()
        : JsonSerializer.Deserialize<Dictionary<string, string>>(SummaryJson) ?? new Dictionary<string, string>();
}
