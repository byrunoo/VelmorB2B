using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;

namespace Commercial.Infrastructure.Security;

/// <summary>
/// Production default until the identity tables exist (later Phase 1 workstream): knows NO principal, company or
/// membership, so every authenticated caller resolves to an identity-only scope without business access (fail closed).
/// This is not a fake authorization source — it grants nothing.
/// </summary>
internal sealed class UnprovisionedIdentityDirectory : IIdentityDirectory
{
    public Task<PrincipalRecord?> FindPrincipalAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        Task.FromResult<PrincipalRecord?>(null);

    public Task<CompanyRecord?> FindCompanyAsync(CompanyId companyId, CancellationToken cancellationToken) =>
        Task.FromResult<CompanyRecord?>(null);

    public Task<IReadOnlyList<WorkforceMembershipRecord>> GetWorkforceMembershipsAsync(PrincipalId principalId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkforceMembershipRecord>>([]);

    public Task<IReadOnlyList<CustomerBindingRecord>> GetCustomerBindingsAsync(PrincipalId principalId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CustomerBindingRecord>>([]);
}
