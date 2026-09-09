namespace SbConsole.Sdk;

public interface IConnectionProvider
{
    Task<IReadOnlyList<ConnectionInfo>> ListAsync(string kind, CancellationToken ct = default);

    /// <summary>Decrypts and returns the secret just-in-time. Null if the connection does not exist.</summary>
    Task<string?> GetSecretAsync(Guid connectionId, CancellationToken ct = default);
}
