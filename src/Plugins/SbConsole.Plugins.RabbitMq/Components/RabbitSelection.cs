using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Components;

/// <summary>The picker's current choice: which saved connection, and which vhost on it.</summary>
public sealed record RabbitSelection(ConnectionInfo Connection, string Vhost)
{
    /// <summary>The query string every RabbitMQ page carries so links stay shareable (spec §7).</summary>
    public string Query => $"connectionId={Connection.Id}&vhost={Uri.EscapeDataString(Vhost)}";
}
