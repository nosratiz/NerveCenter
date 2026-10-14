using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Components;

/// <summary>The Keys browser's current choice: which saved connection, and which database index on it (design spec §2/§7a).</summary>
public sealed record RedisSelection(ConnectionInfo Connection, int Database)
{
    /// <summary>The query string the Keys page carries so links stay shareable, matching RabbitMQ's ConnectionVhostPicker precedent minus the vhost axis.</summary>
    public string Query => $"connectionId={Connection.Id}&database={Database}";
}
