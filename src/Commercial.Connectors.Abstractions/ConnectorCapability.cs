namespace Commercial.Connectors.Abstractions;

/// <summary>
/// Provider-independent capability identifier (e.g. <c>catalog.product.read</c>).
/// Source selection in later phases is capability + authority + policy based, so a portal
/// may obtain different capabilities from different connections. Phase 0 defines only the
/// identifier shape — no capability catalogue, no connector implementation.
/// </summary>
public readonly record struct ConnectorCapability
{
    public ConnectorCapability(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "Capability must be lower-case dot separated segments, e.g. 'catalog.product.read'.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static bool IsValid(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var segments = value.Split('.');
        return segments.Length >= 2 && segments.All(s =>
            s.Length > 0 && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'));
    }
}
