namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>What a binding delivers to: a queue ("q" in management paths) or another exchange ("e").</summary>
public enum BindingDestinationType
{
    Queue,
    Exchange,
}
