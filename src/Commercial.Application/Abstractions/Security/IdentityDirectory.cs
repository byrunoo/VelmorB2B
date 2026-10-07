using Commercial.Application.Abstractions.Scope;
using Commercial.Domain.Identity;

namespace Commercial.Application.Abstractions.Security;

/// <summary>
/// Provider-independent read port over principals and memberships, used ONLY by the execution-scope resolver.
/// Phase 1 WS2 defines the contract; the database-backed implementation arrives with the identity tables
/// (until then Infrastructure registers a fail-closed directory that knows no principal).
///
/// Every lookup is keyed by the caller's own principal — there is no API to enumerate other principals'
/// memberships, so a foreign binding is indistinguishable from a nonexistent one.
/// </summary>
public interface IIdentityDirectory
{
    /// <summary>Principal by authoritative external identity (issuer + subject). Never by e-mail.</summary>
    Task<PrincipalRecord?> FindPrincipalAsync(string issuer, string subject, CancellationToken cancellationToken);

    /// <summary>Company (and its owning tenant) — used to validate a workforce selection.</summary>
    Task<CompanyRecord?> FindCompanyAsync(CompanyId companyId, CancellationToken cancellationToken);

    /// <summary>The workforce principal's company memberships and explicit tenant-level grants.</summary>
    Task<IReadOnlyList<WorkforceMembershipRecord>> GetWorkforceMembershipsAsync(PrincipalId principalId, CancellationToken cancellationToken);

    /// <summary>The customer principal's OWN bindings (any status).</summary>
    Task<IReadOnlyList<CustomerBindingRecord>> GetCustomerBindingsAsync(PrincipalId principalId, CancellationToken cancellationToken);
}

public sealed record PrincipalRecord(PrincipalId Id, PrincipalType Type, bool IsActive);

public sealed record CompanyRecord(CompanyId Id, TenantId TenantId, bool IsActive);

/// <summary>
/// A workforce membership. <c>CompanyId</c> set → company membership with company-level permissions.
/// <c>CompanyId</c> null → tenant-level membership, which is meaningful ONLY together with an explicit grant
/// (<see cref="ScopeGrants.TenantWideCompanyAccess"/>); a null company never implies "all companies" on its own.
/// </summary>
public sealed record WorkforceMembershipRecord(TenantId TenantId, CompanyId? CompanyId, PermissionSet Permissions, ScopeGrants Grants);

public enum CustomerBindingStatus
{
    Invited = 0,
    Active = 1,
    Suspended = 2,
    Revoked = 3,
}

/// <summary>
/// One customer membership (security concept, NOT the commercial CustomerAccount). Permissions are the effective
/// permissions of THIS binding's roles only — never unioned with other bindings.
/// </summary>
public sealed record CustomerBindingRecord(
    CustomerBindingId Id,
    TenantId TenantId,
    CompanyId CompanyId,
    CustomerAccountId CustomerAccountId,
    CustomerBindingStatus Status,
    PermissionSet Permissions);
