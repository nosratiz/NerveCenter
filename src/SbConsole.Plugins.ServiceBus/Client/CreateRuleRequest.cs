namespace SbConsole.Plugins.ServiceBus.Client;

public abstract record CreateRuleRequest(string Name);

public sealed record CreateSqlRuleRequest(string Name, string SqlExpression) : CreateRuleRequest(Name);

public sealed record CreateCorrelationRuleRequest(
    string Name,
    string? CorrelationId,
    string? Label,
    IReadOnlyDictionary<string, string> Properties) : CreateRuleRequest(Name);
