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

    public IReadOnlyList<string> Tags => TagsCsv.Length == 0 ? [] : TagsCsv.Split(',');
}
