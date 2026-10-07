using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;

namespace Commercial.UnitTests.Security;

/// <summary>Phase 1 WS2 — trusted server-side resolution and selection semantics (mandate test list items 21–25 and more).</summary>
public sealed class ExecutionScopeResolverTests
{
    private const string Issuer = "https://idp.example.test";
    private static readonly PermissionSet Owner = PermissionSet.Of(SecurityPermissions.CustomerUsersRead, SecurityPermissions.CustomerUsersManage);
    private static readonly PermissionSet Viewer = PermissionSet.Of(SecurityPermissions.CustomerUsersRead);

    private readonly InMemoryIdentityDirectory _directory = new();
    private readonly ExecutionScopeResolver _resolver;

    public ExecutionScopeResolverTests()
    {
        _resolver = new ExecutionScopeResolver(_directory);
    }

    private static AuthenticatedIdentityContext Identity(string subject, PrincipalType type) => new(Issuer, subject, type, "corr-" + subject);

    private Task<ExecutionScopeResolution> Resolve(string subject, PrincipalType type, CompanyId? company = null, CustomerBindingId? binding = null) =>
        _resolver.ResolveAsync(Identity(subject, type), new ActiveContextRequest(company, binding), CancellationToken.None);

    // ---------- 21: X-Active-Company-Id is selection only ----------

    [Fact]
    public async Task Active_company_header_selects_only_among_the_principals_own_memberships()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var mine = _directory.AddCompany(tenant);
        var other = _directory.AddCompany(tenant);
        var principal = _directory.AddPrincipal(Issuer, "wf-1", PrincipalType.Workforce);
        _directory.AddWorkforceMembership(principal, new WorkforceMembershipRecord(tenant, mine.Id, PermissionSet.Of(SecurityPermissions.CompanyAdminister), ScopeGrants.None));

        var ok = await Resolve("wf-1", PrincipalType.Workforce, company: mine.Id);
        Assert.Equal(ExecutionScopeKind.WorkforceCompany, ok.Scope.Kind);
        Assert.Equal(mine.Id, ok.Scope.ActiveCompanyId);
        Assert.Equal(tenant, ok.Scope.TenantId); // tenant derived from the company, never from the request

        var forged = await Resolve("wf-1", PrincipalType.Workforce, company: other.Id);
        Assert.Equal(ActiveContextError.ActiveCompanyForbidden, forged.Error);
        Assert.False(forged.Scope.HasBusinessScope);

        var unknown = await Resolve("wf-1", PrincipalType.Workforce, company: new CompanyId(Guid.NewGuid()));
        Assert.Equal(ActiveContextError.ActiveCompanyForbidden, unknown.Error); // same answer: no existence oracle
    }

    [Fact]
    public async Task Workforce_without_company_selection_has_no_business_scope_and_company_is_pending()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var a = _directory.AddCompany(tenant);
        var b = _directory.AddCompany(tenant);
        var principal = _directory.AddPrincipal(Issuer, "wf-2", PrincipalType.Workforce);
        _directory.AddWorkforceMembership(principal, new WorkforceMembershipRecord(tenant, a.Id, PermissionSet.Empty, ScopeGrants.None));
        _directory.AddWorkforceMembership(principal, new WorkforceMembershipRecord(tenant, b.Id, PermissionSet.Empty, ScopeGrants.None));

        var result = await Resolve("wf-2", PrincipalType.Workforce);

        Assert.False(result.IsRejected);
        Assert.Equal(ActiveContextError.ActiveCompanyRequired, result.Pending);
        Assert.Equal(ExecutionScopeKind.AuthenticatedIdentity, result.Scope.Kind);
        Assert.Null(result.Scope.TenantId); // never "company null = all companies"
    }

    [Fact]
    public async Task Tenant_wide_workforce_scope_only_with_an_explicit_grant_and_never_unions_foreign_tenants()
    {
        var tenantA = new TenantId(Guid.NewGuid());
        var tenantB = new TenantId(Guid.NewGuid());
        var companyA = _directory.AddCompany(tenantA);
        var companyB = _directory.AddCompany(tenantB);
        var principal = _directory.AddPrincipal(Issuer, "wf-3", PrincipalType.Workforce);
        _directory.AddWorkforceMembership(principal, new WorkforceMembershipRecord(tenantA, null, PermissionSet.Of(SecurityPermissions.TenantAdminister), ScopeGrants.TenantWideCompanyAccess));

        var tenantScope = await Resolve("wf-3", PrincipalType.Workforce);
        Assert.Equal(ExecutionScopeKind.WorkforceTenant, tenantScope.Scope.Kind);
        Assert.Equal(tenantA, tenantScope.Scope.TenantId);
        Assert.Equal(ActiveContextError.ActiveCompanyRequired, tenantScope.Pending);

        var inTenant = await Resolve("wf-3", PrincipalType.Workforce, company: companyA.Id);
        Assert.Equal(ExecutionScopeKind.WorkforceCompany, inTenant.Scope.Kind);
        Assert.True(inTenant.Scope.HasPermission(SecurityPermissions.TenantAdminister));

        var foreign = await Resolve("wf-3", PrincipalType.Workforce, company: companyB.Id);
        Assert.Equal(ActiveContextError.ActiveCompanyForbidden, foreign.Error);
    }

    [Fact]
    public async Task Tenant_level_membership_without_explicit_grant_gives_no_company_access()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var company = _directory.AddCompany(tenant);
        var principal = _directory.AddPrincipal(Issuer, "wf-4", PrincipalType.Workforce);
        _directory.AddWorkforceMembership(principal, new WorkforceMembershipRecord(tenant, null, PermissionSet.Empty, ScopeGrants.None));

        Assert.Equal(ActiveContextError.ActiveCompanyForbidden, (await Resolve("wf-4", PrincipalType.Workforce, company: company.Id)).Error);
        Assert.Equal(ExecutionScopeKind.AuthenticatedIdentity, (await Resolve("wf-4", PrincipalType.Workforce)).Scope.Kind);
    }

    [Fact]
    public async Task Workforce_permissions_come_only_from_the_selected_company()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var a = _directory.AddCompany(tenant);
        var b = _directory.AddCompany(tenant);
        var principal = _directory.AddPrincipal(Issuer, "wf-5", PrincipalType.Workforce);
        _directory.AddWorkforceMembership(principal, new WorkforceMembershipRecord(tenant, a.Id, PermissionSet.Of(SecurityPermissions.CompanyAdminister), ScopeGrants.None));
        _directory.AddWorkforceMembership(principal, new WorkforceMembershipRecord(tenant, b.Id, PermissionSet.Of(SecurityPermissions.SecurityAuditRead), ScopeGrants.None));

        var inB = (await Resolve("wf-5", PrincipalType.Workforce, company: b.Id)).Scope;
        Assert.True(inB.HasPermission(SecurityPermissions.SecurityAuditRead));
        Assert.False(inB.HasPermission(SecurityPermissions.CompanyAdminister));
    }

    [Fact]
    public async Task Workforce_cannot_select_a_customer_binding()
    {
        _directory.AddPrincipal(Issuer, "wf-6", PrincipalType.Workforce);
        var result = await Resolve("wf-6", PrincipalType.Workforce, binding: new CustomerBindingId(Guid.NewGuid()));
        Assert.Equal(ActiveContextError.ActiveCustomerBindingForbidden, result.Error);
    }

    // ---------- 22: X-Active-Customer-Binding-Id is selection only ----------

    [Fact]
    public async Task Customer_binding_header_selects_only_the_callers_own_active_binding()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var company = _directory.AddCompany(tenant);
        var me = _directory.AddPrincipal(Issuer, "cu-1", PrincipalType.Customer);
        var someoneElse = _directory.AddPrincipal(Issuer, "cu-other", PrincipalType.Customer);
        var mine = _directory.AddBinding(me, company, Owner);
        _directory.AddBinding(me, _directory.AddCompany(tenant), Viewer);
        var theirs = _directory.AddBinding(someoneElse, company, Owner);

        var ok = await Resolve("cu-1", PrincipalType.Customer, binding: mine.Id);
        Assert.Equal(ExecutionScopeKind.CustomerBinding, ok.Scope.Kind);
        Assert.Equal(mine.Id, ok.Scope.ActiveCustomerBindingId);
        Assert.Equal(mine.CustomerAccountId, ok.Scope.CustomerAccountId);
        Assert.Equal(mine.CompanyId, ok.Scope.ActiveCompanyId);

        var stolen = await Resolve("cu-1", PrincipalType.Customer, binding: theirs.Id);
        Assert.Equal(ActiveContextError.ActiveCustomerBindingForbidden, stolen.Error);
        Assert.False(stolen.Scope.HasBusinessScope);
    }

    [Fact]
    public async Task Customer_company_header_must_match_the_active_binding()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var me = _directory.AddPrincipal(Issuer, "cu-2", PrincipalType.Customer);
        var binding = _directory.AddBinding(me, _directory.AddCompany(tenant), Viewer);

        Assert.Equal(ActiveContextError.None, (await Resolve("cu-2", PrincipalType.Customer, company: binding.CompanyId, binding: binding.Id)).Error);
        Assert.Equal(ActiveContextError.ActiveCompanyForbidden,
            (await Resolve("cu-2", PrincipalType.Customer, company: new CompanyId(Guid.NewGuid()), binding: binding.Id)).Error);
    }

    // ---------- 23: client-supplied tenant / customer ids cannot create trusted scope ----------

    [Fact]
    public void Selection_request_has_no_tenant_or_customer_account_input()
    {
        var properties = typeof(ActiveContextRequest).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(p => p.Name).ToArray();
        Assert.Equal(["ActiveCompanyId", "ActiveCustomerBindingId"], properties.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(typeof(ContextHeaders).GetFields().Select(f => (string)f.GetValue(null)!),
            h => h.Contains("Tenant", StringComparison.OrdinalIgnoreCase) || h.Contains("Account", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unknown_principal_gets_no_business_scope_whatever_it_selects()
    {
        var anyCompany = _directory.AddCompany(new TenantId(Guid.NewGuid()));

        var none = await Resolve("ghost", PrincipalType.Workforce);
        Assert.Equal(ExecutionScopeKind.AuthenticatedIdentity, none.Scope.Kind);
        Assert.Null(none.Scope.PrincipalId); // PrincipalId is never inferred from the token subject
        Assert.Equal(ActiveContextError.ActiveCompanyForbidden, (await Resolve("ghost", PrincipalType.Workforce, company: anyCompany.Id)).Error);
        Assert.Equal(ActiveContextError.ActiveCustomerBindingForbidden,
            (await Resolve("ghost-c", PrincipalType.Customer, binding: new CustomerBindingId(Guid.NewGuid()))).Error);
    }

    [Fact]
    public async Task Token_principal_type_must_match_the_directory_and_principal_must_be_active()
    {
        _directory.AddPrincipal(Issuer, "mixed", PrincipalType.Customer);
        _directory.AddPrincipal(Issuer, "disabled", PrincipalType.Workforce, isActive: false);

        Assert.Equal(ActiveContextError.ExecutionScopeInvalid, (await Resolve("mixed", PrincipalType.Workforce)).Error);
        Assert.Equal(ActiveContextError.ExecutionScopeInvalid, (await Resolve("disabled", PrincipalType.Workforce)).Error);
    }

    [Fact]
    public async Task Same_subject_from_a_different_issuer_is_a_different_identity()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var me = _directory.AddPrincipal(Issuer, "cu-iss", PrincipalType.Customer);
        _directory.AddBinding(me, _directory.AddCompany(tenant), Owner);

        var foreignIssuer = await _resolver.ResolveAsync(
            new AuthenticatedIdentityContext("https://evil.example.test", "cu-iss", PrincipalType.Customer, "c"), ActiveContextRequest.None, CancellationToken.None);
        Assert.False(foreignIssuer.Scope.HasBusinessScope);
    }

    [Theory]
    [InlineData(null, null, ActiveContextError.None)]
    [InlineData("8a4b8f6e-9a53-4a6c-9a1b-3a5f2a1f0e11", null, ActiveContextError.None)]
    [InlineData("not-a-guid", null, ActiveContextError.ActiveCompanyMalformed)]
    [InlineData("00000000-0000-0000-0000-000000000000", null, ActiveContextError.ActiveCompanyMalformed)]
    [InlineData(null, "{8a4b8f6e-9a53-4a6c-9a1b-3a5f2a1f0e11}", ActiveContextError.ActiveCustomerBindingInvalid)]
    [InlineData(null, "", ActiveContextError.ActiveCustomerBindingInvalid)]
    [InlineData(null, "8a4b8f6e9a534a6c9a1b3a5f2a1f0e11", ActiveContextError.ActiveCustomerBindingInvalid)]
    [InlineData(null, "8a4b8f6e-9a53-4a6c-9a1b-3a5f2a1f0e11", ActiveContextError.None)]
    public void Header_parsing_is_syntax_only_and_strict(string? company, string? binding, ActiveContextError expected)
    {
        var (_, error) = ActiveContextRequest.Parse(company is null ? null : [company], binding is null ? null : [binding]);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Repeated_selection_header_is_malformed()
    {
        var id = Guid.NewGuid().ToString("D");
        Assert.Equal(ActiveContextError.ActiveCustomerBindingInvalid, ActiveContextRequest.Parse(null, [id, id]).Error);
    }

    // ---------- 24: multi-binding selection ----------

    [Fact]
    public async Task Customer_binding_selection_follows_the_0_1_many_rule()
    {
        var tenant = new TenantId(Guid.NewGuid());

        var none = _directory.AddPrincipal(Issuer, "cu-zero", PrincipalType.Customer);
        _directory.AddBinding(none, _directory.AddCompany(tenant), Owner, CustomerBindingStatus.Suspended);
        var zero = await Resolve("cu-zero", PrincipalType.Customer);
        Assert.Equal(ActiveContextError.CustomerContextUnavailable, zero.Pending);
        Assert.False(zero.Scope.HasBusinessScope);

        var single = _directory.AddPrincipal(Issuer, "cu-one", PrincipalType.Customer);
        var only = _directory.AddBinding(single, _directory.AddCompany(tenant), Viewer);
        _directory.AddBinding(single, _directory.AddCompany(tenant), Owner, CustomerBindingStatus.Revoked);
        var one = await Resolve("cu-one", PrincipalType.Customer);
        Assert.Equal(only.Id, one.Scope.ActiveCustomerBindingId); // implicit selection
        Assert.Equal(ActiveContextError.None, one.Pending);

        var multi = _directory.AddPrincipal(Issuer, "cu-many", PrincipalType.Customer);
        var a = _directory.AddBinding(multi, _directory.AddCompany(tenant), Owner);
        var b = _directory.AddBinding(multi, _directory.AddCompany(new TenantId(Guid.NewGuid())), Viewer);
        var many = await Resolve("cu-many", PrincipalType.Customer);
        Assert.Equal(ActiveContextError.ActiveCustomerBindingRequired, many.Pending);
        Assert.False(many.Scope.HasBusinessScope); // never "pick the first" and never a union

        var selectedA = (await Resolve("cu-many", PrincipalType.Customer, binding: a.Id)).Scope;
        var selectedB = (await Resolve("cu-many", PrincipalType.Customer, binding: b.Id)).Scope;
        Assert.True(selectedA.HasPermission(SecurityPermissions.CustomerUsersManage));  // OWNER in A
        Assert.False(selectedB.HasPermission(SecurityPermissions.CustomerUsersManage)); // VIEWER in B
        Assert.NotEqual(selectedA.TenantId, selectedB.TenantId);
    }

    // ---------- 25: non-disclosure ----------

    [Theory]
    [InlineData("foreign")]
    [InlineData("nonexistent")]
    [InlineData("suspended")]
    [InlineData("revoked")]
    [InlineData("invited")]
    public async Task Wrong_or_inactive_binding_errors_are_identical_and_non_disclosing(string kind)
    {
        var tenant = new TenantId(Guid.NewGuid());
        var company = _directory.AddCompany(tenant);
        var me = _directory.AddPrincipal(Issuer, "cu-nd", PrincipalType.Customer);
        var other = _directory.AddPrincipal(Issuer, "cu-nd-other", PrincipalType.Customer);
        _directory.AddBinding(me, company, Viewer);

        var target = kind switch
        {
            "foreign" => _directory.AddBinding(other, company, Owner).Id,
            "nonexistent" => new CustomerBindingId(Guid.NewGuid()),
            "suspended" => _directory.AddBinding(me, company, Owner, CustomerBindingStatus.Suspended).Id,
            "revoked" => _directory.AddBinding(me, company, Owner, CustomerBindingStatus.Revoked).Id,
            _ => _directory.AddBinding(me, company, Owner, CustomerBindingStatus.Invited).Id,
        };

        var result = await Resolve("cu-nd", PrincipalType.Customer, binding: target);

        Assert.Equal(ActiveContextError.ActiveCustomerBindingForbidden, result.Error);
        Assert.Equal(ErrorCodesFor.Forbidden, ActiveContextErrors.Code(result.Error));
        Assert.Equal(ActiveContextErrors.Message(ActiveContextError.ActiveCustomerBindingForbidden), ActiveContextErrors.Message(result.Error));
        Assert.DoesNotContain(target.ToString(), ActiveContextErrors.Message(result.Error), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExecutionScopeKind.AuthenticatedIdentity, result.Scope.Kind);
    }

    private static class ErrorCodesFor
    {
        public const string Forbidden = "ACTIVE_CUSTOMER_BINDING_FORBIDDEN";
    }
}
