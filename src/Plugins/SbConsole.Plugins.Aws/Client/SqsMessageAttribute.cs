namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// One SQS message attribute exactly as SQS returned it: DataType is "String", "Number", "Binary"
/// or a custom-suffixed form ("Number.float", "String.x"), with StringValue set for String/Number
/// types and BinaryValue for Binary ones. Kept whole (not flattened to a string) so Move to source
/// resends every attribute with its original type and bytes.
/// </summary>
public sealed record SqsMessageAttribute(string DataType, string? StringValue, byte[]? BinaryValue)
{
    public bool IsBinary => DataType.StartsWith("Binary", StringComparison.Ordinal);

    /// <summary>What the Receive page shows: the string value, or a byte count for binary data.</summary>
    public string DisplayValue => IsBinary
        ? $"(binary, {BinaryValue?.Length ?? 0} bytes)"
        : StringValue ?? "";
}
