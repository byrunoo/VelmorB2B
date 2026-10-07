using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;

namespace Commercial.UnitTests.Security;

/// <summary>
/// TEST FAKE of <see cref="IIdentityDirectory"/> (also linked into the contract tests). Never registered by any
/// production composition root.
/// </summary>
public sealed class InMemoryIdentityDirectory : IIdentityDirectory
{
    private readonly Dictionary<(string Issuer, string Subject), PrincipalRecord> _principals = [];
    private readonly Dictionary<CompanyId, CompanyRecord> _companies = [];
    private readonly Dictionary<PrincipalId, List<WorkforceMembershipRecord>> _workforce = [];
    private readonly Dictionary<PrincipalId, List<CustomerBindingRecord>> _bindings = [];

    public PrincipalId AddPrincipal(string issuer, string subject, PrincipalType type, bool isActive = true)
    {
        var id = new PrincipalId(Guid.NewGuid());
        _principals[(issuer, subject)] = new PrincipalRecord(id, type, isActive);
        return id;
    }

    public CompanyRecord AddCompany(TenantId tenant, bool isActive = true)
    {
        var company = new CompanyRecord(new CompanyId(Guid.NewGuid()), tenant, isActive);
        _companies[company.Id] = company;
        return company;
    }

    public void AddWorkforceMembership(PrincipalId principal, WorkforceMembershipRecord membership) =>
        Get(_workforce, principal).Add(membership);

    public CustomerBindingRecord AddBinding(
        PrincipalId principal,
        CompanyRecord company,
        PermissionSet permissions,
        CustomerBindingStatus status = CustomerBindingStatus.Active)
    {
        var binding = new CustomerBindingRecord(
            new CustomerBindingId(Guid.NewGuid()), company.TenantId, company.Id, new CustomerAccountId(Guid.NewGuid()), status, permissions);
        Get(_bindings, principal).Add(binding);
        return binding;
    }

    public Task<PrincipalRecord?> FindPrincipalAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        Task.FromResult(_principals.TryGetValue((issuer, subject), out var p) ? p : null);

    public Task<CompanyRecord?> FindCompanyAsync(CompanyId companyId, CancellationToken cancellationToken) =>
        Task.FromResult(_companies.TryGetValue(companyId, out var c) ? c : null);

    public Task<IReadOnlyList<WorkforceMembershipRecord>> GetWorkforceMembershipsAsync(PrincipalId principalId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkforceMembershipRecord>>(_workforce.TryGetValue(principalId, out var m) ? [.. m] : []);

    public Task<IReadOnlyList<CustomerBindingRecord>> GetCustomerBindingsAsync(PrincipalId principalId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CustomerBindingRecord>>(_bindings.TryGetValue(principalId, out var b) ? [.. b] : []);

    private static List<T> Get<T>(Dictionary<PrincipalId, List<T>> map, PrincipalId key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        return list;
    }
}
