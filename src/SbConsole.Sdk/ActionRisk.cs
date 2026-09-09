namespace SbConsole.Sdk;

/// <summary>Risk level of a plugin action; the host enforces confirmation rules from it.</summary>
public enum ActionRisk
{
    Safe = 0,
    Mutating = 1,
    Destructive = 2,
}
