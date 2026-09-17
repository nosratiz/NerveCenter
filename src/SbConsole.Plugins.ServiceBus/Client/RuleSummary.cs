namespace SbConsole.Plugins.ServiceBus.Client;

public abstract record RuleSummary(string Name);

public sealed record SqlRuleSummary(string Name, string SqlExpression) : RuleSummary(Name);

public sealed record CorrelationRuleSummary(
    string Name,
    string? CorrelationId,
    string? Label,
    IReadOnlyDictionary<string, string> Properties) : RuleSummary(Name);

public sealed record OtherRuleSummary(string Name, string RawFilterText) : RuleSummary(Name);
