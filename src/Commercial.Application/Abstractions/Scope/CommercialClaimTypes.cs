namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// Claim contract shared by all authentication schemes. Claims carry AUTHENTICATED IDENTITY only (stage 1):
/// issuer, subject and principal type. There are deliberately NO tenant / company / customer-account claims — business
/// scope is always resolved server-side from memberships (stage 2) and never trusted from a token or request.
/// </summary>
public static class CommercialClaimTypes
{
    public const string Issuer = "iss";
    public const string Subject = "sub";
    public const string PrincipalType = "principal_type";
}
