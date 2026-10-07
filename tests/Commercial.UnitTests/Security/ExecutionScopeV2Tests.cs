using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;
using Commercial.Infrastructure.Messaging.Outbox;
using Commercial.Infrastructure.Persistence.Session;

namespace Commercial.UnitTests.Security;

/// <summary>Phase 1 WS2 — ExecutionScope v2 semantics (mandate test list items 1–20).</summary>
public sealed class ExecutionScopeV2Tests
{
    private static readonly PrincipalId Principal = new(Guid.NewGuid());
    private static readonly TenantId TenantA = new(Guid.NewGuid());
    private static readonly TenantId TenantB = new(Guid.NewGuid());
    private static readonly CompanyId CompanyA1 = new(Guid.NewGuid());
    private static readonly CompanyId CompanyB1 = new(Guid.NewGuid());

    private static readonly PermissionSet Owner = PermissionSet.Of(
        SecurityPermissions.CustomerUsersRead, SecurityPermissions.CustomerUsersInvite, SecurityPermissions.CustomerUsersManage, SecurityPermissions.CustomerRolesAssign);

    private static readonly PermissionSet Viewer = PermissionSet.Of(SecurityPermissions.CustomerUsersRead);

    private static ResolvedCustomerBinding Binding(TenantId tenant, CompanyId company, PermissionSet permissions) =>
        new(new CustomerBindingId(Guid.NewGuid()), tenant, company, new CustomerAccountId(Guid.NewGuid()), permissions);

    [Fact] // 1
    public void Valid_workforce_company_scope()
    {
        var scope = ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Of(SecurityPermissions.CompanyAdminister), "c1");

        Assert.Equal(ExecutionScopeKind.WorkforceCompany, scope.Kind);
        Assert.Equal(PrincipalType.Workforce, scope.PrincipalType);
        Assert.Equal(TenantA, scope.TenantId);
        Assert.Equal(CompanyA1, scope.ActiveCompanyId);
        Assert.Null(scope.CustomerAccountId);
        Assert.Null(scope.ActiveCustomerBindingId);
        Assert.True(scope.HasBusinessScope);
        Assert.True(scope.HasPermission(SecurityPermissions.CompanyAdminister));
        Assert.False(scope.HasPermission(SecurityPermissions.TenantAdminister));
    }

    [Fact] // 2
    public void Workforce_without_company_fails_closed_unless_an_explicit_tenant_wide_grant_exists()
    {
        var ex = Assert.Throws<InvalidExecutionScopeException>(() =>
            ExecutionScope.WorkforceTenant(Principal, TenantA, ScopeGrants.None, PermissionSet.Empty, "c2"));
        Assert.Contains(ex.Violations, v => v.Contains("TenantWideCompanyAccess", StringComparison.Ordinal));

        var tenantWide = ExecutionScope.WorkforceTenant(Principal, TenantA, ScopeGrants.TenantWideCompanyAccess, PermissionSet.Empty, "c2");
        Assert.Null(tenantWide.ActiveCompanyId);
        Assert.Equal(ScopeGrants.TenantWideCompanyAccess, tenantWide.Grants);
    }

    [Fact] // 3
    public void Valid_customer_binding_scope_takes_tenant_company_account_from_the_binding()
    {
        var binding = Binding(TenantA, CompanyA1, Viewer);
        var scope = ExecutionScope.CustomerBinding(Principal, binding, "c3");

        Assert.Equal(ExecutionScopeKind.CustomerBinding, scope.Kind);
        Assert.Equal(binding.TenantId, scope.TenantId);
        Assert.Equal(binding.CompanyId, scope.ActiveCompanyId);
        Assert.Equal(binding.CustomerAccountId, scope.CustomerAccountId);
        Assert.Equal(binding.BindingId, scope.ActiveCustomerBindingId);
        Assert.Equal(ScopeGrants.None, scope.Grants);
    }

    [Fact] // 4, 5, 6 — the record cannot be built without binding/account/company; the validator also rejects partial CUSTOMER rows.
    public void Customer_scope_requires_binding_account_and_company()
    {
        Assert.Throws<ArgumentNullException>(() => ExecutionScope.CustomerBinding(Principal, null!, "c4"));
        Assert.Throws<ArgumentNullException>(() => ExecutionScope.CustomerBinding(Principal, Binding(TenantA, CompanyA1, null!), "c4"));

        // A CUSTOMER identity scope (no binding) is never a business scope.
        var identityOnly = ExecutionScope.ForIdentity(new AuthenticatedIdentityContext("iss", "sub", PrincipalType.Customer, "c4"), Principal);
        Assert.False(identityOnly.HasBusinessScope);
        Assert.Null(identityOnly.ActiveCustomerBindingId);
        Assert.Null(identityOnly.CustomerAccountId);
        Assert.Null(identityOnly.ActiveCompanyId);
    }

    [Fact] // 7
    public void Customer_scope_can_never_model_a_tenant_wide_grant()
    {
        // No CustomerBinding factory parameter accepts grants; the only tenant-level factory is WORKFORCE-only.
        var customerFactory = typeof(ExecutionScope).GetMethod(nameof(ExecutionScope.CustomerBinding))!;
        Assert.DoesNotContain(customerFactory.GetParameters(), p => p.ParameterType == typeof(ScopeGrants));
        var scope = ExecutionScope.CustomerBinding(Principal, Binding(TenantA, CompanyA1, Owner), "c7");
        Assert.Equal(ScopeGrants.None, scope.Grants);
        Assert.Equal(ExecutionScopeKind.WorkforceTenant,
            ExecutionScope.WorkforceTenant(Principal, TenantA, ScopeGrants.TenantWideCompanyAccess, PermissionSet.Empty, "c7").Kind);
    }

    [Fact] // 8
    public void Customer_scope_permissions_come_only_from_the_supplied_binding()
    {
        var factory = typeof(ExecutionScope).GetMethod(nameof(ExecutionScope.CustomerBinding))!;
        Assert.DoesNotContain(factory.GetParameters(), p => p.ParameterType == typeof(PermissionSet));

        var viewerBinding = Binding(TenantA, CompanyA1, Viewer);
        var scope = ExecutionScope.CustomerBinding(Principal, viewerBinding, "c8");
        Assert.Equal(Viewer.CanonicalCodes, scope.Permissions.CanonicalCodes);
        Assert.False(scope.HasPermission(SecurityPermissions.CustomerUsersManage));
    }

    [Fact] // 9
    public void Same_principal_builds_separate_valid_scopes_in_tenant_A_and_tenant_B()
    {
        var a = ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Empty, "c9");
        var b = ExecutionScope.WorkforceCompany(Principal, TenantB, CompanyB1, PermissionSet.Empty, "c9");

        Assert.Equal(a.PrincipalId, b.PrincipalId);
        Assert.NotEqual(a.TenantId, b.TenantId);
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact] // 10, 11
    public void Same_principal_is_owner_in_binding_A_and_viewer_in_binding_B_without_union()
    {
        var ownerScope = ExecutionScope.CustomerBinding(Principal, Binding(TenantA, CompanyA1, Owner), "c10");
        var viewerScope = ExecutionScope.CustomerBinding(Principal, Binding(TenantB, CompanyB1, Viewer), "c10");

        Assert.True(ownerScope.HasPermission(SecurityPermissions.CustomerUsersManage));
        Assert.False(viewerScope.HasPermission(SecurityPermissions.CustomerUsersManage)); // OWNER in A does not leak into B
        Assert.True(viewerScope.HasPermission(SecurityPermissions.CustomerUsersRead));
        Assert.Single(viewerScope.Permissions);
    }

    [Fact] // 12, 13
    public void System_business_scope_requires_tenant_and_company()
    {
        var factory = typeof(ExecutionScope).GetMethod(nameof(ExecutionScope.SystemBusiness))!;
        var parameters = factory.GetParameters();
        Assert.Equal(typeof(TenantId), parameters[0].ParameterType);  // non-nullable: tenant required
        Assert.Equal(typeof(CompanyId), parameters[1].ParameterType); // non-nullable: company required

        Assert.Throws<InvalidExecutionScopeException>(() => new OutboxScopeMetadata(TenantA, null, null, null).ToExecutionScope("c12", "m1"));
        Assert.Throws<InvalidExecutionScopeException>(() => new OutboxScopeMetadata(null, CompanyA1, null, null).ToExecutionScope("c13", "m1"));
        Assert.Throws<InvalidExecutionScopeException>(() => ExecutionScope.SystemBusiness(TenantA, CompanyA1, null, new CustomerBindingId(Guid.NewGuid()), "c13"));

        var system = new OutboxScopeMetadata(TenantA, CompanyA1, null, null).ToExecutionScope("c13", "m1");
        Assert.Equal(ExecutionScopeKind.SystemBusiness, system.Kind);
        Assert.Empty(system.Permissions);

        var infra = OutboxScopeMetadata.None.ToExecutionScope("c13", "m2");
        Assert.Equal(ExecutionScopeKind.SystemInfrastructure, infra.Kind);
        Assert.False(infra.HasBusinessScope); // never an unrestricted SYSTEM fallback
    }

    public static TheoryData<string, Func<ExecutionScope>> InvalidCombinations => new()
    {
        { "blank correlation", () => ExecutionScope.Anonymous(" ") },
        { "workforce company: undefined grant bits", () => ExecutionScope.WorkforceTenant(Principal, TenantA, (ScopeGrants)8, PermissionSet.Empty, "x") },
        { "system binding without account", () => ExecutionScope.SystemBusiness(TenantA, CompanyA1, null, new CustomerBindingId(Guid.NewGuid()), "x") },
        { "identity scope for SYSTEM", () => ExecutionScope.ForIdentity(null!, null) },
        { "blank workforce correlation", () => ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Empty, "") },
    };

    [Theory] // 14
    [MemberData(nameof(InvalidCombinations))]
    public void Invalid_principal_scope_combinations_are_rejected(string name, Func<ExecutionScope> create)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.ThrowsAny<Exception>(() => create());
    }

    [Fact] // 14 (stage 1)
    public void Http_identity_can_never_be_system_or_anonymous()
    {
        Assert.Throws<ArgumentException>(() => new AuthenticatedIdentityContext("iss", "sub", PrincipalType.System, "c"));
        Assert.Throws<ArgumentException>(() => new AuthenticatedIdentityContext("iss", "sub", PrincipalType.Anonymous, "c"));
        Assert.Throws<ArgumentException>(() => new AuthenticatedIdentityContext("", "sub", PrincipalType.Workforce, "c"));
    }

    [Fact] // 15
    public void Fingerprint_is_deterministic_and_order_independent_for_permissions()
    {
        var p1 = PermissionSet.Of(SecurityPermissions.CompanyAdminister, SecurityPermissions.IdentityAdminister);
        var p2 = PermissionSet.Of(SecurityPermissions.IdentityAdminister, SecurityPermissions.CompanyAdminister);
        var a = ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, p1, "c15");
        var b = ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, p2, "c15");

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.StartsWith(ScopeFingerprint.Prefix, a.Fingerprint.Value, StringComparison.Ordinal);
        Assert.Equal(ScopeFingerprint.Prefix.Length + 64, a.Fingerprint.Value.Length); // SHA-256 hex
        Assert.Equal(12, a.Fingerprint.ShortId.Length);
    }

    public static TheoryData<string, Func<ExecutionScope>> SecurityRelevantChanges => new()
    {
        { "tenant", () => ExecutionScope.WorkforceCompany(Principal, TenantB, CompanyA1, PermissionSet.Empty, "c") },
        { "company", () => ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyB1, PermissionSet.Empty, "c") },
        { "principal", () => ExecutionScope.WorkforceCompany(new PrincipalId(Guid.NewGuid()), TenantA, CompanyA1, PermissionSet.Empty, "c") },
        { "permission", () => ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Of(SecurityPermissions.SecurityAuditRead), "c") },
        { "kind/grant", () => ExecutionScope.WorkforceTenant(Principal, TenantA, ScopeGrants.TenantWideCompanyAccess, PermissionSet.Empty, "c") },
        { "principal type", () => ExecutionScope.SystemBusiness(TenantA, CompanyA1, null, null, "c") },
    };

    [Theory] // 16
    [MemberData(nameof(SecurityRelevantChanges))]
    public void Security_relevant_change_changes_the_fingerprint(string change, Func<ExecutionScope> changed)
    {
        var baseline = ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Empty, "c");
        Assert.True(baseline.Fingerprint != changed().Fingerprint, change);
    }

    [Fact] // 16 (customer binding switch)
    public void Switching_customer_binding_creates_a_new_scope_with_a_new_fingerprint()
    {
        var a = ExecutionScope.CustomerBinding(Principal, Binding(TenantA, CompanyA1, Viewer), "c");
        var b = ExecutionScope.CustomerBinding(Principal, Binding(TenantA, CompanyA1, Viewer), "c");
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact] // 17 — decision: correlation / causation are excluded from the authorization fingerprint.
    public void Correlation_change_alone_does_not_change_the_fingerprint()
    {
        var a = ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Empty, "corr-1", "cause-1");
        var b = ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Empty, "corr-2", "cause-2");
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Scope_is_immutable()
    {
        Assert.All(typeof(ExecutionScope).GetProperties(), p => Assert.False(p.CanWrite, $"{p.Name} must not be settable"));
        Assert.Empty(typeof(ExecutionScope).GetConstructors()); // only validated factories
    }

    [Fact] // 18
    public void Nested_ambient_scopes_restore_the_previous_value()
    {
        var accessor = new AmbientExecutionScopeAccessor();
        var outer = ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Empty, "outer");
        var inner = ExecutionScope.CustomerBinding(Principal, Binding(TenantB, CompanyB1, Viewer), "inner");

        using (accessor.Begin(outer))
        {
            using (accessor.Begin(inner))
            {
                Assert.Same(inner, accessor.Current);
            }

            Assert.Same(outer, accessor.Current);
        }

        Assert.Same(ExecutionScope.Unscoped, accessor.Current);
    }

    [Fact] // 19
    public async Task Exception_path_restores_the_previous_scope()
    {
        var accessor = new AmbientExecutionScopeAccessor();
        var outer = ExecutionScope.WorkforceCompany(Principal, TenantA, CompanyA1, PermissionSet.Empty, "outer");

        using (accessor.Begin(outer))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                using (accessor.Begin(ExecutionScope.CustomerBinding(Principal, Binding(TenantB, CompanyB1, Owner), "inner")))
                {
                    await Task.Yield();
                    throw new InvalidOperationException("boom");
                }
            });

            Assert.Same(outer, accessor.Current);
        }
    }

    [Fact] // 20
    public async Task Parallel_flows_with_different_business_scopes_do_not_leak()
    {
        var accessor = new AmbientExecutionScopeAccessor();
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            var expected = i % 2 == 0
                ? ExecutionScope.WorkforceCompany(Principal, TenantA, new CompanyId(Guid.NewGuid()), PermissionSet.Empty, $"flow-{i}")
                : ExecutionScope.CustomerBinding(Principal, Binding(TenantB, CompanyB1, Viewer), $"flow-{i}");
            using var _ = accessor.Begin(expected);
            await Task.Delay(Random.Shared.Next(1, 8));
            var afterAwait = accessor.Current;
            await Task.Run(() => Task.Delay(1));
            return (Expected: expected, AfterAwait: afterAwait, AfterHop: accessor.Current);
        }));

        Assert.All(results, r =>
        {
            Assert.Same(r.Expected, r.AfterAwait);
            Assert.Same(r.Expected, r.AfterHop);
        });
        Assert.Same(ExecutionScope.Unscoped, accessor.Current);
    }

    [Fact]
    public void Db_session_mapping_is_one_way_complete_and_clears_business_keys_for_non_business_scopes()
    {
        var customer = ExecutionScope.CustomerBinding(Principal, Binding(TenantA, CompanyA1, Viewer), "db");
        var mapped = DbSessionContext.FromScope(customer);
        Assert.Equal(DbSessionContext.Keys, mapped.Settings.Select(s => s.Key));
        Assert.Equal(TenantA.ToString(), mapped[DbSessionContext.TenantId]);
        Assert.Equal(CompanyA1.ToString(), mapped[DbSessionContext.CompanyId]);
        Assert.Equal(customer.ActiveCustomerBindingId!.Value.ToString(), mapped[DbSessionContext.CustomerBindingId]);
        Assert.Equal("CUSTOMER", mapped[DbSessionContext.PrincipalType]);
        Assert.Equal("false", mapped[DbSessionContext.TenantWideCompanyAccess]);

        var tenantWide = DbSessionContext.FromScope(ExecutionScope.WorkforceTenant(Principal, TenantA, ScopeGrants.TenantWideCompanyAccess, PermissionSet.Empty, "db"));
        Assert.Equal("true", tenantWide[DbSessionContext.TenantWideCompanyAccess]);
        Assert.Equal(string.Empty, tenantWide[DbSessionContext.CompanyId]);

        foreach (var scope in new[] { ExecutionScope.Unscoped, ExecutionScope.SystemInfrastructure("db") })
        {
            var none = DbSessionContext.FromScope(scope);
            Assert.Equal(DbSessionContext.Keys, none.Settings.Select(s => s.Key)); // every key always written
            Assert.Equal(string.Empty, none[DbSessionContext.TenantId]);
            Assert.Equal(string.Empty, none[DbSessionContext.CompanyId]);
            Assert.Equal(string.Empty, none[DbSessionContext.CustomerAccountId]);
            Assert.Equal(string.Empty, none[DbSessionContext.CustomerBindingId]);
        }
    }

    [Theory]
    [InlineData("customer.users.manage", true)]
    [InlineData("tenant.administer", true)]
    [InlineData("OWNER", false)]
    [InlineData("customer", false)]
    [InlineData("Customer.Users", false)]
    [InlineData("", false)]
    public void Permission_codes_are_explicit_dotted_codes_not_role_names(string code, bool valid)
    {
        if (valid)
        {
            Assert.Equal(code, new Permission(code).Code);
        }
        else
        {
            Assert.ThrowsAny<ArgumentException>(() => new Permission(code));
        }
    }
}
