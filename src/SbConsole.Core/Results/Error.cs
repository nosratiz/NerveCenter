namespace SbConsole.Core.Results;

public sealed record Error(ErrorCategory Category, string Message);
