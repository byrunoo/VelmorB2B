using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;

namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// Result of stage-2 resolution.
/// <list type="bullet">
/// <item><see cref="Error"/> ≠ None: the request is rejected immediately (malformed / forbidden selection, invalid scope).</item>
/// <item><see cref="Pending"/> ≠ None: no business scope was established; endpoints that REQUIRE one fail with this error,
/// endpoints that only need identity (e.g. "who am I", "list my memberships") still work.</item>
/// </list>
/// </summary>
public sealed record ExecutionScopeResolution(ExecutionScope Scope, ActiveContextError Error, ActiveContextError Pending)
{
    public bool IsRejected => Error != ActiveContextError.None;

    public static ExecutionScopeResolution Resolved(ExecutionScope scope) => new(scope, ActiveContextError.None, ActiveContextError.None);

    public static ExecutionScopeResolution WithPending(ExecutionScope scope, ActiveContextError pending) => new(scope, ActiveContextError.None, pending);

    public static ExecutionScopeResolution Rejected(ExecutionScope fallback, ActiveContextError error) => new(fallback, error, ActiveContextError.None);
}

/// <summary>Trusted server-side stage-2 resolution: identity + selection hints → ONE active business scope.</summary>
public interface IExecutionScopeResolver
{
    Task<ExecutionScopeResolution> ResolveAsync(
        AuthenticatedIdentityContext identity,
        ActiveContextRequest selection,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider-independent resolver over <see cref="IIdentityDirectory"/>. Rules (fail closed):
/// <list type="bullet">
/// <item>Unknown principal → identity-only scope; any selection header → FORBIDDEN (non-disclosing).</item>
/// <item>Directory principal type ≠ token principal type, or inactive principal → EXECUTION_SCOPE_INVALID.</item>
/// <item>WORKFORCE: company header must match a company membership or an explicit tenant-wide grant of the company's
/// tenant; no header → tenant-level scope ONLY with an explicit grant in exactly one tenant, else ACTIVE_COMPANY_REQUIRED
/// pending. A binding header is forbidden.</item>
/// <item>CUSTOMER: 0 active bindings → unavailable; 1 → implicit; &gt;1 without header → ACTIVE_CUSTOMER_BINDING_REQUIRED;
/// unknown / foreign / inactive binding → ACTIVE_CUSTOMER_BINDING_FORBIDDEN (identical). Permissions come from the
/// active binding only. Company header, if sent, must equal the binding's company.</item>
/// </list>
/// </summary>
public sealed class ExecutionScopeResolver(IIdentityDirectory directory) : IExecutionScopeResolver
{
    public async Task<ExecutionScopeResolution> ResolveAsync(
        AuthenticatedIdentityContext identity,
        ActiveContextRequest selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(selection);

        var principal = await directory.FindPrincipalAsync(identity.Issuer, identity.Subject, cancellationToken);
        if (principal is null)
        {
            var unknown = ExecutionScope.ForIdentity(identity, null);
            if (selection.ActiveCustomerBindingId is not null)
            {
                return ExecutionScopeResolution.Rejected(unknown, ActiveContextError.ActiveCustomerBindingForbidden);
            }

            if (selection.ActiveCompanyId is not null)
            {
                return ExecutionScopeResolution.Rejected(unknown, ActiveContextError.ActiveCompanyForbidden);
            }

            return ExecutionScopeResolution.WithPending(unknown, identity.PrincipalType == PrincipalType.Customer
                ? ActiveContextError.CustomerContextUnavailable
                : ActiveContextError.ActiveCompanyRequired);
        }

        if (principal.Type != identity.PrincipalType || !principal.IsActive)
        {
            return ExecutionScopeResolution.Rejected(ExecutionScope.ForIdentity(identity, null), ActiveContextError.ExecutionScopeInvalid);
        }

        return identity.PrincipalType == PrincipalType.Workforce
            ? await ResolveWorkforceAsync(identity, principal.Id, selection, cancellationToken)
            : await ResolveCustomerAsync(identity, principal.Id, selection, cancellationToken);
    }

    private async Task<ExecutionScopeResolution> ResolveWorkforceAsync(
        AuthenticatedIdentityContext identity,
        PrincipalId principalId,
        ActiveContextRequest selection,
        CancellationToken cancellationToken)
    {
        var identityScope = ExecutionScope.ForIdentity(identity, principalId);
        if (selection.ActiveCustomerBindingId is not null)
        {
            return ExecutionScopeResolution.Rejected(identityScope, ActiveContextError.ActiveCustomerBindingForbidden);
        }

        var memberships = await directory.GetWorkforceMembershipsAsync(principalId, cancellationToken);

        if (selection.ActiveCompanyId is not { } companyId)
        {
            // No selection: tenant-level scope only with an explicit grant, and only when unambiguous (one tenant).
            var tenants = memberships.Select(m => m.TenantId).Distinct().ToList();
            var tenantGrants = memberships
                .Where(m => m.CompanyId is null && m.Grants.HasFlag(ScopeGrants.TenantWideCompanyAccess))
                .ToList();
            if (tenants.Count == 1 && tenantGrants.Count > 0)
            {
                var scope = ExecutionScope.WorkforceTenant(principalId, tenants[0], ScopeGrants.TenantWideCompanyAccess,
                    PermissionSet.Of(tenantGrants.SelectMany(g => g.Permissions)), identity.CorrelationId);
                return ExecutionScopeResolution.WithPending(scope, ActiveContextError.ActiveCompanyRequired);
            }

            return ExecutionScopeResolution.WithPending(identityScope, ActiveContextError.ActiveCompanyRequired);
        }

        var company = await directory.FindCompanyAsync(companyId, cancellationToken);
        if (company is null || !company.IsActive)
        {
            return ExecutionScopeResolution.Rejected(identityScope, ActiveContextError.ActiveCompanyForbidden);
        }

        // Only memberships of THIS company and explicit tenant-wide grants of THIS company's tenant contribute.
        var applicable = memberships
            .Where(m => m.TenantId == company.TenantId
                && (m.CompanyId == company.Id
                    || (m.CompanyId is null && m.Grants.HasFlag(ScopeGrants.TenantWideCompanyAccess))))
            .ToList();
        if (applicable.Count == 0)
        {
            return ExecutionScopeResolution.Rejected(identityScope, ActiveContextError.ActiveCompanyForbidden);
        }

        return ExecutionScopeResolution.Resolved(ExecutionScope.WorkforceCompany(
            principalId, company.TenantId, company.Id, PermissionSet.Of(applicable.SelectMany(m => m.Permissions)), identity.CorrelationId));
    }

    private async Task<ExecutionScopeResolution> ResolveCustomerAsync(
        AuthenticatedIdentityContext identity,
        PrincipalId principalId,
        ActiveContextRequest selection,
        CancellationToken cancellationToken)
    {
        var identityScope = ExecutionScope.ForIdentity(identity, principalId);
        var active = (await directory.GetCustomerBindingsAsync(principalId, cancellationToken))
            .Where(b => b.Status == CustomerBindingStatus.Active)
            .ToList();

        CustomerBindingRecord binding;
        if (selection.ActiveCustomerBindingId is { } requested)
        {
            // Foreign, unknown and inactive bindings are indistinguishable: same code, same message.
            var match = active.FirstOrDefault(b => b.Id == requested);
            if (match is null)
            {
                return ExecutionScopeResolution.Rejected(identityScope, ActiveContextError.ActiveCustomerBindingForbidden);
            }

            binding = match;
        }
        else
        {
            switch (active.Count)
            {
                case 0:
                    return selection.ActiveCompanyId is null
                        ? ExecutionScopeResolution.WithPending(identityScope, ActiveContextError.CustomerContextUnavailable)
                        : ExecutionScopeResolution.Rejected(identityScope, ActiveContextError.ActiveCompanyForbidden);
                case 1:
                    binding = active[0];
                    break;
                default:
                    return ExecutionScopeResolution.WithPending(identityScope, ActiveContextError.ActiveCustomerBindingRequired);
            }
        }

        if (selection.ActiveCompanyId is { } company && company != binding.CompanyId)
        {
            return ExecutionScopeResolution.Rejected(identityScope, ActiveContextError.ActiveCompanyForbidden);
        }

        return ExecutionScopeResolution.Resolved(ExecutionScope.CustomerBinding(
            principalId,
            new ResolvedCustomerBinding(binding.Id, binding.TenantId, binding.CompanyId, binding.CustomerAccountId, binding.Permissions),
            identity.CorrelationId));
    }
}
