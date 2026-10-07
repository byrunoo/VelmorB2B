using System.Collections;
using System.Text.RegularExpressions;

namespace Commercial.Application.Abstractions.Security;

/// <summary>
/// An explicit permission code (e.g. <c>customer.users.manage</c>). Authorization code asks
/// <c>scope.HasPermission(permission)</c> — never "role == OWNER". Roles are bundles of permissions resolved before the
/// execution scope is built.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Permission is the security domain term.")]
public readonly partial record struct Permission
{
    public Permission(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !Format().IsMatch(code))
        {
            throw new ArgumentException("Permission codes are lower-case dotted segments, e.g. 'customer.users.manage'.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }

    public override string ToString() => Code;

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex Format();
}

/// <summary>Immutable, ordered set of effective permissions of ONE resolved scope.</summary>
public sealed class PermissionSet : IReadOnlyCollection<Permission>
{
    private readonly SortedSet<string> _codes;

    private PermissionSet(IEnumerable<string> codes)
    {
        _codes = new SortedSet<string>(codes, StringComparer.Ordinal);
    }

    public static PermissionSet Empty { get; } = new([]);

    public int Count => _codes.Count;

    public static PermissionSet Of(params Permission[] permissions) => Of((IEnumerable<Permission>)permissions);

    public static PermissionSet Of(IEnumerable<Permission> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return new PermissionSet(permissions.Select(p => p.Code));
    }

    public bool Has(Permission permission) => _codes.Contains(permission.Code);

    /// <summary>Stable canonical representation (sorted, ordinal) used by the scope fingerprint.</summary>
    public IReadOnlyList<string> CanonicalCodes => [.. _codes];

    public IEnumerator<Permission> GetEnumerator() => _codes.Select(c => new Permission(c)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Phase 1 permission catalogue — identity / security administration only. No Product / Order / Invoice permissions
/// (those arrive with their phases). Customer roles (OWNER, BUYER, VIEWER, FINANCE) are permission bundles defined
/// by data, never checked by name in code.
/// </summary>
public static class SecurityPermissions
{
    public static readonly Permission TenantAdminister = new("tenant.administer");
    public static readonly Permission CompanyAdminister = new("company.administer");
    public static readonly Permission IdentityAdminister = new("identity.administer");
    public static readonly Permission SecurityAdminister = new("security.administer");
    public static readonly Permission SecurityAuditRead = new("security.audit.read");
    public static readonly Permission CustomerUsersRead = new("customer.users.read");
    public static readonly Permission CustomerUsersInvite = new("customer.users.invite");
    public static readonly Permission CustomerUsersManage = new("customer.users.manage");
    public static readonly Permission CustomerRolesAssign = new("customer.roles.assign");

    public static IReadOnlyList<Permission> All { get; } =
    [
        TenantAdminister, CompanyAdminister, IdentityAdminister, SecurityAdminister, SecurityAuditRead,
        CustomerUsersRead, CustomerUsersInvite, CustomerUsersManage, CustomerRolesAssign,
    ];
}

/// <summary>
/// Explicit scope grants. Missing scope never implies broader access: tenant-wide company access exists only as this
/// explicit grant (never "CompanyId = null means all companies") and is forbidden for CUSTOMER scopes.
/// </summary>
[Flags]
#pragma warning disable CA1711 // "Grants" is the domain term.
public enum ScopeGrants
#pragma warning restore CA1711
{
    None = 0,
    TenantWideCompanyAccess = 1,
}
