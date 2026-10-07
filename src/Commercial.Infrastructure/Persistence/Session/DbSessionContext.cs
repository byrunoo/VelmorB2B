using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;

namespace Commercial.Infrastructure.Persistence.Session;

/// <summary>
/// PREPARATION ONLY (Phase 1 WS2): the one-way mapping ExecutionScope → PostgreSQL session keys that the future
/// RLS session initializer (next workstream, ADR-0001) will write with <c>set_config(key, value, true)</c>.
/// Nothing calls this against a database yet; no RLS policy exists.
///
/// Direction is strictly scope → database. The database never feeds values back into a scope, and keys are always
/// written in full (empty string for "not set") so a pooled connection never keeps a previous request's value.
/// Maintenance (BYPASSRLS) never goes through this mapping.
/// </summary>
public sealed record DbSessionContext(IReadOnlyList<KeyValuePair<string, string>> Settings)
{
    public const string ScopeKind = "commercial.scope_kind";
    public const string PrincipalType = "commercial.principal_type";
    public const string PrincipalId = "commercial.principal_id";
    public const string TenantId = "commercial.tenant_id";
    public const string CompanyId = "commercial.company_id";
    public const string CustomerAccountId = "commercial.customer_account_id";
    public const string CustomerBindingId = "commercial.customer_binding_id";
    public const string TenantWideCompanyAccess = "commercial.tenant_wide_company_access";
    public const string ScopeFingerprint = "commercial.scope_fingerprint";

    /// <summary>Every key, in write order. All are always written.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        ScopeKind, PrincipalType, PrincipalId, TenantId, CompanyId, CustomerAccountId, CustomerBindingId,
        TenantWideCompanyAccess, ScopeFingerprint,
    ];

    public static DbSessionContext FromScope(ExecutionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Only business scopes carry business keys; every other kind maps to "no business access".
        var business = scope.HasBusinessScope;
        return new DbSessionContext(
        [
            new(ScopeKind, scope.Kind.ToString()),
            new(PrincipalType, PrincipalTypeValues.ToWire(scope.PrincipalType)),
            new(PrincipalId, scope.PrincipalId?.ToString() ?? string.Empty),
            new(TenantId, business ? scope.TenantId?.ToString() ?? string.Empty : string.Empty),
            new(CompanyId, business ? scope.ActiveCompanyId?.ToString() ?? string.Empty : string.Empty),
            new(CustomerAccountId, business ? scope.CustomerAccountId?.ToString() ?? string.Empty : string.Empty),
            new(CustomerBindingId, business ? scope.ActiveCustomerBindingId?.ToString() ?? string.Empty : string.Empty),
            new(TenantWideCompanyAccess, business && scope.Grants.HasFlag(ScopeGrants.TenantWideCompanyAccess) ? "true" : "false"),
            new(ScopeFingerprint, scope.Fingerprint.Value),
        ]);
    }

    public string this[string key] => Settings.Single(s => s.Key == key).Value;
}
