namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// STAGE 1 of the two-stage security context: who authenticated, nothing more. Built only from a validated
/// authentication result (issuer + subject + principal-type claim). It carries NO tenant / company / customer
/// assumptions and grants no business access by itself. Stage 2 (<see cref="ExecutionScope"/>) is resolved from it by
/// <see cref="IExecutionScopeResolver"/>.
/// </summary>
public sealed record AuthenticatedIdentityContext
{
    public AuthenticatedIdentityContext(string issuer, string subject, PrincipalType principalType, string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        if (principalType is not (PrincipalType.Workforce or PrincipalType.Customer))
        {
            // SYSTEM is never an HTTP identity; anonymous callers have no identity context.
            throw new ArgumentException("Authenticated identities are WORKFORCE or CUSTOMER.", nameof(principalType));
        }

        Issuer = issuer;
        Subject = subject;
        PrincipalType = principalType;
        CorrelationId = correlationId;
    }

    public string Issuer { get; }

    public string Subject { get; }

    public PrincipalType PrincipalType { get; }

    public string CorrelationId { get; }
}
