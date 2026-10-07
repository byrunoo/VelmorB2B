using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;

namespace Commercial.Application.Abstractions.Scope;

/// <summary>What kind of security context a scope represents.</summary>
public enum ExecutionScopeKind
{
    /// <summary>Unauthenticated; no business access.</summary>
    Anonymous = 0,

    /// <summary>Authenticated (stage 1, optionally with a resolved principal) but no active business scope.</summary>
    AuthenticatedIdentity = 1,

    /// <summary>WORKFORCE, one tenant, NO company, explicit tenant-wide grant required.</summary>
    WorkforceTenant = 2,

    /// <summary>WORKFORCE, one tenant, exactly one active company.</summary>
    WorkforceCompany = 3,

    /// <summary>CUSTOMER, exactly one active customer binding (tenant + company + customer account from the binding).</summary>
    CustomerBinding = 4,

    /// <summary>SYSTEM infrastructure processing (e.g. outbox dispatch); no business access.</summary>
    SystemInfrastructure = 5,

    /// <summary>SYSTEM business processing from trusted job/outbox metadata: tenant + company required.</summary>
    SystemBusiness = 6,
}

/// <summary>
/// The RESOLVED, IMMUTABLE security context of one unit of work (stage 2 of the two-stage model). Future PostgreSQL RLS
/// (ADR-0001) is driven one-way from this object.
///
/// Invariant: ONE PRINCIPAL MAY HAVE MANY MEMBERSHIPS. ONE NORMAL REQUEST HAS ONE ACTIVE BUSINESS SCOPE.
///
/// Instances are created only through the validated factories below; nothing is mutable. Changing company or customer
/// binding means creating a NEW scope (new fingerprint). Invalid or partial combinations fail closed with
/// <see cref="InvalidExecutionScopeException"/>.
/// </summary>
public sealed class ExecutionScope
{
    private ExecutionScope(
        ExecutionScopeKind kind,
        PrincipalType principalType,
        PrincipalId? principalId,
        TenantId? tenantId,
        CompanyId? activeCompanyId,
        CustomerAccountId? customerAccountId,
        CustomerBindingId? activeCustomerBindingId,
        PermissionSet permissions,
        ScopeGrants grants,
        string correlationId,
        string? causationId)
    {
        Kind = kind;
        PrincipalType = principalType;
        PrincipalId = principalId;
        TenantId = tenantId;
        ActiveCompanyId = activeCompanyId;
        CustomerAccountId = customerAccountId;
        ActiveCustomerBindingId = activeCustomerBindingId;
        Permissions = permissions;
        Grants = grants;
        CorrelationId = correlationId;
        CausationId = causationId;

        var violations = ExecutionScopeValidator.Validate(this);
        if (violations.Count > 0)
        {
            throw new InvalidExecutionScopeException(violations);
        }

        Fingerprint = ScopeFingerprint.Compute(this);
    }

    public ExecutionScopeKind Kind { get; }

    public PrincipalType PrincipalType { get; }

    public PrincipalId? PrincipalId { get; }

    public TenantId? TenantId { get; }

    public CompanyId? ActiveCompanyId { get; }

    public CustomerAccountId? CustomerAccountId { get; }

    public CustomerBindingId? ActiveCustomerBindingId { get; }

    public PermissionSet Permissions { get; }

    public ScopeGrants Grants { get; }

    public string CorrelationId { get; }

    public string? CausationId { get; }

    /// <summary>Digest of the security-relevant fields (excludes correlation/causation).</summary>
    public ScopeFingerprint Fingerprint { get; }

    public bool IsAuthenticated => Kind is not (ExecutionScopeKind.Anonymous or ExecutionScopeKind.SystemInfrastructure);

    /// <summary>True when the scope grants business data access (tenant-scoped rows).</summary>
    public bool HasBusinessScope => Kind is ExecutionScopeKind.WorkforceTenant or ExecutionScopeKind.WorkforceCompany
        or ExecutionScopeKind.CustomerBinding or ExecutionScopeKind.SystemBusiness;

    public bool HasPermission(Permission permission) => Permissions.Has(permission);

    /// <summary>Used when code runs outside any established scope (no business access).</summary>
    public static ExecutionScope Unscoped { get; } = Anonymous("unscoped");

    public static ExecutionScope Anonymous(string correlationId) =>
        new(ExecutionScopeKind.Anonymous, PrincipalType.Anonymous, null, null, null, null, null, PermissionSet.Empty, ScopeGrants.None, correlationId, null);

    /// <summary>Stage-1 identity without an active business scope (principal may be unresolved).</summary>
    public static ExecutionScope ForIdentity(AuthenticatedIdentityContext identity, PrincipalId? principalId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(ExecutionScopeKind.AuthenticatedIdentity, identity.PrincipalType, principalId, null, null, null, null,
            PermissionSet.Empty, ScopeGrants.None, identity.CorrelationId, null);
    }

    /// <summary>WORKFORCE with exactly one active company.</summary>
    public static ExecutionScope WorkforceCompany(
        PrincipalId principalId,
        TenantId tenantId,
        CompanyId activeCompanyId,
        PermissionSet permissions,
        string correlationId,
        string? causationId = null) =>
        new(ExecutionScopeKind.WorkforceCompany, PrincipalType.Workforce, principalId, tenantId, activeCompanyId, null, null,
            permissions ?? throw new ArgumentNullException(nameof(permissions)), ScopeGrants.None, correlationId, causationId);

    /// <summary>WORKFORCE tenant-level scope: only with the explicit tenant-wide grant (never implied by a missing company).</summary>
    public static ExecutionScope WorkforceTenant(
        PrincipalId principalId,
        TenantId tenantId,
        ScopeGrants grants,
        PermissionSet permissions,
        string correlationId,
        string? causationId = null) =>
        new(ExecutionScopeKind.WorkforceTenant, PrincipalType.Workforce, principalId, tenantId, null, null, null,
            permissions ?? throw new ArgumentNullException(nameof(permissions)), grants, correlationId, causationId);

    /// <summary>
    /// CUSTOMER under exactly one active binding. Tenant / company / account AND permissions all come from that one
    /// binding — there is no parameter through which another binding's permissions could be supplied.
    /// </summary>
    public static ExecutionScope CustomerBinding(
        PrincipalId principalId,
        ResolvedCustomerBinding binding,
        string correlationId,
        string? causationId = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new(ExecutionScopeKind.CustomerBinding, PrincipalType.Customer, principalId, binding.TenantId, binding.CompanyId,
            binding.CustomerAccountId, binding.BindingId, binding.Permissions ?? throw new ArgumentNullException(nameof(binding)), ScopeGrants.None, correlationId, causationId);
    }

    /// <summary>SYSTEM infrastructure processing without any business scope.</summary>
    public static ExecutionScope SystemInfrastructure(string correlationId, string? causationId = null) =>
        new(ExecutionScopeKind.SystemInfrastructure, PrincipalType.System, null, null, null, null, null,
            PermissionSet.Empty, ScopeGrants.None, correlationId, causationId);

    /// <summary>SYSTEM business processing from trusted persisted metadata (never from HTTP input).</summary>
    public static ExecutionScope SystemBusiness(
        TenantId tenantId,
        CompanyId companyId,
        CustomerAccountId? customerAccountId,
        CustomerBindingId? customerBindingId,
        string correlationId,
        string? causationId = null) =>
        new(ExecutionScopeKind.SystemBusiness, PrincipalType.System, null, tenantId, companyId, customerAccountId, customerBindingId,
            PermissionSet.Empty, ScopeGrants.None, correlationId, causationId);
}

/// <summary>The server-side binding row (with THIS binding's effective permissions) an active CUSTOMER scope is built from.</summary>
public sealed record ResolvedCustomerBinding(
    CustomerBindingId BindingId,
    TenantId TenantId,
    CompanyId CompanyId,
    CustomerAccountId CustomerAccountId,
    PermissionSet Permissions);

public sealed class InvalidExecutionScopeException : InvalidOperationException
{
    public const string Code = "EXECUTION_SCOPE_INVALID";

    public InvalidExecutionScopeException(IReadOnlyList<string> violations)
        : base($"{Code}: {string.Join("; ", violations)}")
    {
        Violations = violations;
    }

    public IReadOnlyList<string> Violations { get; }
}
