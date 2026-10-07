using System.Net;
using System.Text.Json;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Commercial.Domain.Identity;
using Commercial.UnitTests.Security;
using Json.Schema;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Commercial.ContractTests;

/// <summary>
/// API host with an in-memory TEST directory (the production default directory resolves nobody). Development
/// authentication supplies stage-1 identity only (issuer urn:commercial:development + subject + principal type).
/// </summary>
public sealed class ActiveContextApiFactory : ContractApiFactory
{
    private const string DevIssuer = "urn:commercial:development";

    public ActiveContextApiFactory()
    {
        var tenantA = new TenantId(Guid.NewGuid());
        var tenantB = new TenantId(Guid.NewGuid());
        CompanyA1 = Directory.AddCompany(tenantA);
        CompanyA2 = Directory.AddCompany(tenantA);
        var companyB1 = Directory.AddCompany(tenantB);

        var workforce = Directory.AddPrincipal(DevIssuer, "wf-contract", PrincipalType.Workforce);
        Directory.AddWorkforceMembership(workforce, new WorkforceMembershipRecord(tenantA, CompanyA1.Id, PermissionSet.Empty, ScopeGrants.None));

        var multi = Directory.AddPrincipal(DevIssuer, "cu-multi", PrincipalType.Customer);
        OwnerBinding = Directory.AddBinding(multi, CompanyA1, PermissionSet.Of(SecurityPermissions.CustomerUsersRead, SecurityPermissions.CustomerUsersManage));
        ViewerBinding = Directory.AddBinding(multi, companyB1, PermissionSet.Of(SecurityPermissions.CustomerUsersRead));
        SuspendedBinding = Directory.AddBinding(multi, CompanyA2, PermissionSet.Empty, CustomerBindingStatus.Suspended);

        var other = Directory.AddPrincipal(DevIssuer, "cu-other", PrincipalType.Customer);
        ForeignBinding = Directory.AddBinding(other, CompanyA1, PermissionSet.Of(SecurityPermissions.CustomerUsersManage));

        Directory.AddPrincipal(DevIssuer, "cu-as-workforce", PrincipalType.Customer);
    }

    public InMemoryIdentityDirectory Directory { get; } = new();

    public CompanyRecord CompanyA1 { get; }

    public CompanyRecord CompanyA2 { get; }

    public CustomerBindingRecord OwnerBinding { get; }

    public CustomerBindingRecord ViewerBinding { get; }

    public CustomerBindingRecord SuspendedBinding { get; }

    public CustomerBindingRecord ForeignBinding { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<IIdentityDirectory>(Directory));
    }
}

/// <summary>Phase 1 WS2 — HTTP active-context selection contract (headers, error codes, non-disclosure).</summary>
public sealed class ActiveContextContractTests : IClassFixture<ActiveContextApiFactory>, IClassFixture<ContractApiFactory>
{
    private static JsonSchema Schema => ErrorEnvelopeContractTests.Schema; // registered once per process

    private readonly ActiveContextApiFactory _factory;
    private readonly ContractApiFactory _unprovisioned;

    public ActiveContextContractTests(ActiveContextApiFactory factory, ContractApiFactory unprovisioned)
    {
        _factory = factory;
        _unprovisioned = unprovisioned;
    }

    public static TheoryData<string, string, string, string?, string?, HttpStatusCode, string> ErrorCases => new()
    {
        // case, principal type, subject, active company, active binding (tokens resolved below), status, code
        { "workforce without company", "WORKFORCE", "wf-contract", null, null, HttpStatusCode.BadRequest, "ACTIVE_COMPANY_REQUIRED" },
        { "workforce foreign company", "WORKFORCE", "wf-contract", "A2", null, HttpStatusCode.Forbidden, "ACTIVE_COMPANY_FORBIDDEN" },
        { "workforce malformed company", "WORKFORCE", "wf-contract", "nope", null, HttpStatusCode.BadRequest, "BAD_REQUEST" },
        { "customer with many bindings, none selected", "CUSTOMER", "cu-multi", null, null, HttpStatusCode.BadRequest, "ACTIVE_CUSTOMER_BINDING_REQUIRED" },
        { "customer foreign binding", "CUSTOMER", "cu-multi", null, "FOREIGN", HttpStatusCode.Forbidden, "ACTIVE_CUSTOMER_BINDING_FORBIDDEN" },
        { "customer suspended binding", "CUSTOMER", "cu-multi", null, "SUSPENDED", HttpStatusCode.Forbidden, "ACTIVE_CUSTOMER_BINDING_FORBIDDEN" },
        { "customer malformed binding", "CUSTOMER", "cu-multi", null, "not-a-guid", HttpStatusCode.BadRequest, "ACTIVE_CUSTOMER_BINDING_INVALID" },
        { "customer company mismatch", "CUSTOMER", "cu-multi", "A2", "OWNER", HttpStatusCode.Forbidden, "ACTIVE_COMPANY_FORBIDDEN" },
        { "token type differs from directory", "WORKFORCE", "cu-as-workforce", null, null, HttpStatusCode.Forbidden, "EXECUTION_SCOPE_INVALID" },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task Active_context_errors_use_the_standard_envelope(
        string name, string type, string subject, string? company, string? binding, HttpStatusCode status, string code)
    {
        var (response, body, json) = await SendAsync(_factory, "/api/v1/system/access-probe/active-company", type, subject, Token(company), Token(binding), "ctx-" + code);

        Assert.True(status == response.StatusCode, $"{name}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(Schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, body);
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal("ctx-" + code, json.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Foreign_and_nonexistent_bindings_are_indistinguishable()
    {
        var (foreign, foreignBody, f) = await SendAsync(_factory, "/api/v1/system/me", "CUSTOMER", "cu-multi", null, _factory.ForeignBinding.Id.ToString(), "nd");
        var (missing, missingBody, m) = await SendAsync(_factory, "/api/v1/system/me", "CUSTOMER", "cu-multi", null, Guid.NewGuid().ToString("D"), "nd");

        Assert.Equal(foreign.StatusCode, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        foreach (var property in new[] { "type", "title", "status", "code", "detail", "errors" })
        {
            Assert.Equal(f.GetProperty(property).GetRawText(), m.GetProperty(property).GetRawText());
        }

        Assert.DoesNotContain(_factory.ForeignBinding.Id.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_factory.ForeignBinding.CustomerAccountId.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cu-other", foreignBody, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(missingBody));
    }

    [Fact]
    public async Task Permissions_come_from_the_active_binding_only()
    {
        var path = "/api/v1/system/access-probe/customer-users-manage";
        var (owner, _, _) = await SendAsync(_factory, path, "CUSTOMER", "cu-multi", null, _factory.OwnerBinding.Id.ToString(), "perm-owner");
        var (viewer, _, v) = await SendAsync(_factory, path, "CUSTOMER", "cu-multi", null, _factory.ViewerBinding.Id.ToString(), "perm-viewer");
        var (unselected, _, u) = await SendAsync(_factory, path, "CUSTOMER", "cu-multi", null, null, "perm-none");

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);            // OWNER in A
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);    // VIEWER in B: no union with A
        Assert.Equal("FORBIDDEN", v.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, unselected.StatusCode);
        Assert.Equal("ACTIVE_CUSTOMER_BINDING_REQUIRED", u.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Me_reports_the_resolved_scope_and_ignores_client_supplied_tenant_or_account_headers()
    {
        var binding = _factory.OwnerBinding;
        var (response, _, json) = await SendAsync(
            _factory, "/api/v1/system/me", "CUSTOMER", "cu-multi", null, binding.Id.ToString(), "me-1",
            ("X-Tenant-Id", Guid.NewGuid().ToString()), ("X-Customer-Account-Id", Guid.NewGuid().ToString()), ("X-Dev-Tenant-Id", Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CUSTOMER", json.GetProperty("principalType").GetString());
        Assert.Equal("CustomerBinding", json.GetProperty("scopeKind").GetString());
        Assert.Equal(binding.TenantId.Value, json.GetProperty("tenantId").GetGuid());
        Assert.Equal(binding.CompanyId.Value, json.GetProperty("activeCompanyId").GetGuid());
        Assert.Equal(binding.CustomerAccountId.Value, json.GetProperty("customerAccountId").GetGuid());
        Assert.Equal(binding.Id.Value, json.GetProperty("activeCustomerBindingId").GetGuid());
        Assert.StartsWith("sfp1-", json.GetProperty("scopeFingerprint").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("pendingSelection").ValueKind);
    }

    [Fact]
    public async Task Identity_only_requests_still_work_and_report_the_pending_selection()
    {
        var (response, _, json) = await SendAsync(_factory, "/api/v1/system/me", "CUSTOMER", "cu-multi", null, null, "me-2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AuthenticatedIdentity", json.GetProperty("scopeKind").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("tenantId").ValueKind);
        Assert.Equal("ACTIVE_CUSTOMER_BINDING_REQUIRED", json.GetProperty("pendingSelection").GetString());
    }

    [Fact]
    public async Task Workforce_selects_one_active_company()
    {
        var (response, _, json) = await SendAsync(_factory, "/api/v1/system/me", "WORKFORCE", "wf-contract", _factory.CompanyA1.Id.ToString(), null, "me-3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("WorkforceCompany", json.GetProperty("scopeKind").GetString());
        Assert.Equal(_factory.CompanyA1.Id.Value, json.GetProperty("activeCompanyId").GetGuid());
        Assert.Equal(_factory.CompanyA1.TenantId.Value, json.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task Production_default_directory_resolves_no_business_scope()
    {
        var (me, _, json) = await SendAsync(_unprovisioned, "/api/v1/system/me", "WORKFORCE", "anyone", null, null, "unprov-1");
        var (probe, _, p) = await SendAsync(_unprovisioned, "/api/v1/system/access-probe/active-company", "WORKFORCE", "anyone", null, null, "unprov-2");
        var (selected, _, s) = await SendAsync(_unprovisioned, "/api/v1/system/me", "WORKFORCE", "anyone", Guid.NewGuid().ToString(), null, "unprov-3");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("AuthenticatedIdentity", json.GetProperty("scopeKind").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("principalId").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, probe.StatusCode);
        Assert.Equal("ACTIVE_COMPANY_REQUIRED", p.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, selected.StatusCode);
        Assert.Equal("ACTIVE_COMPANY_FORBIDDEN", s.GetProperty("code").GetString());
    }

    private string? Token(string? value) => value switch
    {
        "A2" => _factory.CompanyA2.Id.ToString(),
        "OWNER" => _factory.OwnerBinding.Id.ToString(),
        "FOREIGN" => _factory.ForeignBinding.Id.ToString(),
        "SUSPENDED" => _factory.SuspendedBinding.Id.ToString(),
        _ => value,
    };

    private static async Task<(HttpResponseMessage Response, string Body, JsonElement Json)> SendAsync(
        ContractApiFactory factory,
        string path,
        string principalType,
        string subject,
        string? activeCompany,
        string? activeBinding,
        string correlationId,
        params (string Name, string Value)[] extraHeaders)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Correlation-Id", correlationId);
        request.Headers.Add("X-Dev-Principal-Type", principalType);
        request.Headers.Add("X-Dev-Subject", subject);
        if (activeCompany is not null)
        {
            request.Headers.TryAddWithoutValidation(ContextHeaders.ActiveCompanyId, activeCompany);
        }

        if (activeBinding is not null)
        {
            request.Headers.TryAddWithoutValidation(ContextHeaders.ActiveCustomerBindingId, activeBinding);
        }

        foreach (var (name, value) in extraHeaders)
        {
            request.Headers.Add(name, value);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, body, JsonDocument.Parse(body).RootElement.Clone());
    }
}
