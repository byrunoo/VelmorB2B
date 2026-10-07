namespace Commercial.Infrastructure.Persistence.Session;

/// <summary>
/// Internal security failure of the database session scope (ADR-0001). Never mapped to a client-visible error: the API
/// answers a generic 500 <c>INTERNAL_ERROR</c>; the code is for logs / future security audit. Messages never contain
/// tenant / company / customer values.
/// </summary>
public sealed class DbSessionScopeException : InvalidOperationException
{
    /// <summary>Ambient ExecutionScope fingerprint ≠ fingerprint recorded from the PostgreSQL session.</summary>
    public const string ScopeMismatch = "DB_SESSION_SCOPE_MISMATCH";

    /// <summary>Command on a connection that was not opened through the session initializer.</summary>
    public const string NotInitialized = "DB_SESSION_NOT_INITIALIZED";

    /// <summary>Invalid scope, pool reset disabled, or PostgreSQL did not confirm every key.</summary>
    public const string InitializationFailed = "DB_SESSION_INITIALIZATION_FAILED";

    /// <summary>Application SQL tried to change <c>commercial.*</c> session keys itself.</summary>
    public const string KeyWriteForbidden = "DB_SESSION_KEY_WRITE_FORBIDDEN";

    public DbSessionScopeException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public DbSessionScopeException(string code, string message, Exception innerException)
        : base($"{code}: {message}", innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
