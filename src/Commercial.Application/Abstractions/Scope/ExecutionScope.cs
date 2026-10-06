using Commercial.Domain.Identity;

namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// Server-side execution context of the current unit of work (HTTP request, background job,
/// outbox dispatch). Future tenant isolation (ADR-0001) reads from this single source.
/// Phase 0 carries the values but does NOT enforce isolation with them.
/// </summary>
public sealed record ExecutionScope
{
    public required PrincipalType PrincipalType { get; init; }

    public string? PrincipalId { get; init; }

    public TenantId? TenantId { get; init; }

    public CompanyId? CompanyId { get; init; }

    public CustomerAccountId? CustomerAccountId { get; init; }

    public SalesChannelId? SalesChannelId { get; init; }

    public required string CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public bool IsAuthenticated => PrincipalType is not PrincipalType.Anonymous;

    /// <summary>Used when code runs outside any established scope.</summary>
    public static ExecutionScope Unscoped { get; } = new()
    {
        PrincipalType = PrincipalType.Anonymous,
        CorrelationId = "unscoped",
    };

    public static ExecutionScope Anonymous(string correlationId) => new()
    {
        PrincipalType = PrincipalType.Anonymous,
        CorrelationId = correlationId,
    };

    public static ExecutionScope ForSystem(string correlationId, string? causationId = null, TenantId? tenantId = null) => new()
    {
        PrincipalType = PrincipalType.System,
        PrincipalId = "system",
        CorrelationId = correlationId,
        CausationId = causationId,
        TenantId = tenantId,
    };
}
