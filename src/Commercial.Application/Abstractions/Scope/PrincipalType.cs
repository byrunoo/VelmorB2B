namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// Principal kinds. The claim contract (see <see cref="CommercialClaimTypes"/>) uses the
/// upper-case wire values <c>WORKFORCE</c> and <c>CUSTOMER</c>.
/// </summary>
public enum PrincipalType
{
    Anonymous = 0,

    /// <summary>Internal staff (admin / internal sales UI).</summary>
    Workforce = 1,

    /// <summary>B2B customer user, always bound to a customer account.</summary>
    Customer = 2,

    /// <summary>Background execution (worker, outbox dispatch).</summary>
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

    public static string? ToWire(PrincipalType type) => type switch
    {
        PrincipalType.Workforce => Workforce,
        PrincipalType.Customer => Customer,
        _ => null,
    };
}
