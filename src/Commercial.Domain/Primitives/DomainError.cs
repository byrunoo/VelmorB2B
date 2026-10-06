namespace Commercial.Domain.Primitives;

/// <summary>
/// Provider-independent, serializable error primitive. <see cref="Code"/> is a stable,
/// machine-readable UPPER_SNAKE_CASE identifier that clients may branch on.
/// </summary>
public sealed record DomainError
{
    public DomainError(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);
        Code = code;
        Message = message;
    }

    public string Code { get; }

    public string Message { get; }

    public static DomainError None { get; } = new("NONE", string.Empty);
}
