namespace SbConsole.Sdk;

/// <summary>One label/value row shown in a detailed confirmation dialog.</summary>
public sealed record ConfirmationDetail(string Label, string Value);

/// <summary>
/// A detailed confirmation: what is about to happen, to what, with supporting rows.
/// <paramref name="Target"/> is also the text the user must type when typed confirmation applies.
/// </summary>
public sealed record ConfirmationRequest(
    string Verb,
    string Target,
    bool IsProd,
    string? Consequence = null,
    IReadOnlyList<ConfirmationDetail>? Details = null,
    string? AuditAction = null,
    bool Destructive = false);
