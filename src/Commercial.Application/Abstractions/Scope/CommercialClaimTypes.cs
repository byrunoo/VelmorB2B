namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// Claim contract shared by all authentication schemes. Phase 1 replaces the development
/// scheme with real identity but must keep issuing these claims.
/// </summary>
public static class CommercialClaimTypes
{
    public const string PrincipalType = "principal_type";
    public const string Subject = "sub";
    public const string TenantId = "tenant_id";
    public const string CompanyId = "company_id";
    public const string CustomerAccountId = "customer_account_id";
    public const string SalesChannelId = "sales_channel_id";
}
