namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// Principal kinds. The claim contract (see <see cref="CommercialClaimTypes"/>) uses the
/// upper-case wire values <c>WORKFORCE</c> and <c>CUSTOMER</c>. There is deliberately no generic ADMIN type, and
/// maintenance (BYPASSRLS tooling) is never a scope principal.
/// </summary>
public enum PrincipalType
{
    Anonymous = 0,

    /// <summary>Internal staff (admin / internal sales UI); one active company per request.</summary>
    Workforce = 1,

    /// <summary>B2B customer user; business scope always comes from ONE active customer binding per request.</summary>
    Customer = 2,

    /// <summary>Background execution from trusted job/outbox metadata only; never an HTTP identity, never unrestricted.</summary>
    System = 3,
}

public static class PrincipalTypeValues
{
    public const string Workforce = "WORKFORCE";
    public const string Customer = "CUSTOMER";

    public static PrincipalType Parse(string? value) => value switch
    {
        Workforce => PrincipalType.Workforce,
        Customer => PrincipalType.Customer,
        _ => PrincipalType.Anonymous,
    };

    public static string ToWire(PrincipalType type) => type switch
    {
        PrincipalType.Workforce => Workforce,
        PrincipalType.Customer => Customer,
        PrincipalType.System => "SYSTEM",
        _ => "ANONYMOUS",
    };
}
