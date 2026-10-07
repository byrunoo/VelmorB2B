using Commercial.Application.Abstractions.Security;

namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// Centralized execution-scope rules (fail closed). Every <see cref="ExecutionScope"/> is validated at construction;
/// the rule set is also usable directly (tests, persisted metadata).
/// </summary>
public static class ExecutionScopeValidator
{
    public static IReadOnlyList<string> Validate(ExecutionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var v = new List<string>();

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                v.Add(message);
            }
        }

        bool tenant = scope.TenantId is not null, company = scope.ActiveCompanyId is not null,
            account = scope.CustomerAccountId is not null, binding = scope.ActiveCustomerBindingId is not null,
            principal = scope.PrincipalId is not null, tenantWide = scope.Grants.HasFlag(ScopeGrants.TenantWideCompanyAccess);

        Require(!string.IsNullOrWhiteSpace(scope.CorrelationId), "CorrelationId is required");
        Require(Enum.IsDefined(scope.Grants), "unknown grants");

        switch (scope.Kind)
        {
            case ExecutionScopeKind.Anonymous:
                Require(scope.PrincipalType == PrincipalType.Anonymous, "Anonymous scope must have principal type Anonymous");
                Require(!principal && !tenant && !company && !account && !binding, "Anonymous scope carries no principal or business scope");
                Require(scope.Permissions.Count == 0 && scope.Grants == ScopeGrants.None, "Anonymous scope carries no permissions or grants");
                break;

            case ExecutionScopeKind.AuthenticatedIdentity:
                Require(scope.PrincipalType is PrincipalType.Workforce or PrincipalType.Customer, "Identity scope must be WORKFORCE or CUSTOMER");
                Require(!tenant && !company && !account && !binding, "Identity scope carries no business scope");
                Require(scope.Permissions.Count == 0 && scope.Grants == ScopeGrants.None, "Identity scope carries no permissions or grants");
                break;

            case ExecutionScopeKind.WorkforceCompany:
                Require(scope.PrincipalType == PrincipalType.Workforce, "WorkforceCompany scope requires WORKFORCE");
                Require(principal, "WORKFORCE scope requires PrincipalId");
                Require(tenant, "WORKFORCE scope requires TenantId");
                Require(company, "ACTIVE_COMPANY_REQUIRED: company-scoped WORKFORCE scope requires ActiveCompanyId");
                Require(!account && !binding, "WORKFORCE scope must not carry customer account or customer binding");
                Require(scope.Grants == ScopeGrants.None, "WorkforceCompany scope carries no tenant-wide grant");
                break;

            case ExecutionScopeKind.WorkforceTenant:
                Require(scope.PrincipalType == PrincipalType.Workforce, "WorkforceTenant scope requires WORKFORCE");
                Require(principal, "WORKFORCE scope requires PrincipalId");
                Require(tenant, "WORKFORCE scope requires TenantId");
                Require(!company, "WorkforceTenant scope has no active company");
                Require(tenantWide, "tenant-level WORKFORCE scope requires the explicit TenantWideCompanyAccess grant (missing company never implies tenant-wide)");
                Require(!account && !binding, "WORKFORCE scope must not carry customer account or customer binding");
                break;

            case ExecutionScopeKind.CustomerBinding:
                Require(scope.PrincipalType == PrincipalType.Customer, "CustomerBinding scope requires CUSTOMER");
                Require(principal, "CUSTOMER scope requires PrincipalId");
                Require(tenant, "CUSTOMER scope requires TenantId");
                Require(company, "CUSTOMER scope requires ActiveCompanyId");
                Require(account, "CUSTOMER scope requires CustomerAccountId");
                Require(binding, "ACTIVE_CUSTOMER_BINDING_REQUIRED: CUSTOMER scope requires ActiveCustomerBindingId");
                Require(scope.Grants == ScopeGrants.None, "CUSTOMER scope can never carry a tenant-wide grant");
                break;

            case ExecutionScopeKind.SystemInfrastructure:
                Require(scope.PrincipalType == PrincipalType.System, "SystemInfrastructure scope requires SYSTEM");
                Require(!principal && !tenant && !company && !account && !binding, "SystemInfrastructure scope carries no business scope");
                Require(scope.Permissions.Count == 0 && scope.Grants == ScopeGrants.None, "SYSTEM scope carries no permissions or grants");
                break;

            case ExecutionScopeKind.SystemBusiness:
                Require(scope.PrincipalType == PrincipalType.System, "SystemBusiness scope requires SYSTEM");
                Require(!principal, "SYSTEM scope has no PrincipalId");
                Require(tenant, "SYSTEM business scope requires TenantId");
                Require(company, "SYSTEM business scope requires CompanyId");
                Require(!binding || account, "a SYSTEM customer-binding context also requires CustomerAccountId");
                Require(scope.Permissions.Count == 0 && scope.Grants == ScopeGrants.None, "SYSTEM scope carries no permissions or grants");
                break;

            default:
                v.Add($"unknown scope kind {scope.Kind}");
                break;
        }

        return v;
    }
}
