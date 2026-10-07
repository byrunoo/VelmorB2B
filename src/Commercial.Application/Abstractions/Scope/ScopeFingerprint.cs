using System.Security.Cryptography;
using System.Text.Json;

namespace Commercial.Application.Abstractions.Scope;

/// <summary>
/// Deterministic, non-secret digest of the security-relevant resolved scope: kind, principal type, principal id,
/// tenant, active company, customer account, active customer binding, grants and the canonical (sorted) permission
/// codes. Correlation / causation ids are deliberately EXCLUDED (they do not change authorization).
///
/// Canonical input is a fixed-order JSON document (no concatenation ambiguity) hashed with SHA-256. No tokens,
/// passwords or non-authorization PII are included. Safe to log as <see cref="ShortId"/>.
/// </summary>
public readonly record struct ScopeFingerprint
{
    public const string Prefix = "sfp1-";

    private ScopeFingerprint(string value)
    {
        Value = value;
    }

    public string Value { get; }

    /// <summary>First 12 hex chars — for logs / diagnostics only.</summary>
    public string ShortId => Value.Substring(Prefix.Length, 12);

    public override string ToString() => Value;

    internal static ScopeFingerprint Compute(ExecutionScope scope)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", 1);
            json.WriteString("kind", scope.Kind.ToString());
            json.WriteString("principalType", scope.PrincipalType.ToString());
            WriteNullable(json, "principalId", scope.PrincipalId?.Value);
            WriteNullable(json, "tenantId", scope.TenantId?.Value);
            WriteNullable(json, "companyId", scope.ActiveCompanyId?.Value);
            WriteNullable(json, "customerAccountId", scope.CustomerAccountId?.Value);
            WriteNullable(json, "customerBindingId", scope.ActiveCustomerBindingId?.Value);
            json.WriteNumber("grants", (int)scope.Grants);
            json.WriteStartArray("permissions");
            foreach (var code in scope.Permissions.CanonicalCodes)
            {
                json.WriteStringValue(code);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return new ScopeFingerprint(Prefix + Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan)));
    }

    private static void WriteNullable(Utf8JsonWriter json, string name, Guid? value)
    {
        if (value is { } v)
        {
            json.WriteString(name, v.ToString("D"));
        }
        else
        {
            json.WriteNull(name);
        }
    }
}
