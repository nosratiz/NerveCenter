namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// Deliberately separate from PeekStart (Earliest/Latest/Offset) even though they overlap -- Peek
/// never needs a Timestamp mode, and coupling the two would force Peek's UI to handle a mode it
/// can't use. See design spec §2.
/// </summary>
public enum OffsetResetMode { Earliest, Latest, Offset, Timestamp }
