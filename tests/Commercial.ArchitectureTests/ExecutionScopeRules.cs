using System.Reflection;
using System.Text.RegularExpressions;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Abstractions.Security;
using Commercial.ArchitectureTests.Support;

namespace Commercial.ArchitectureTests;

/// <summary>
/// Phase 1 WS2 — ExecutionScope v2 / active-context guardrails (docs/architecture/execution-scope.md).
/// Invariant: ONE PRINCIPAL MAY HAVE MANY MEMBERSHIPS. ONE NORMAL REQUEST HAS ONE ACTIVE BUSINESS SCOPE.
/// Failure code: EXECUTION_SCOPE_CONTRACT_VIOLATION.
/// </summary>
public sealed partial class ExecutionScopeRules
{
    private const string Failure = "EXECUTION_SCOPE_CONTRACT_VIOLATION";

    /// <summary>Request headers that would let a client assert business scope directly. Never allowed.</summary>
    private static readonly string[] TrustedScopeShortcutHeaders =
    [
        "X-Tenant-Id", "X-Company-Id", "X-Customer-Account-Id", "X-Customer-Id", "X-Sales-Channel-Id",
        "X-Dev-Tenant-Id", "X-Dev-Company-Id", "X-Dev-Customer-Account-Id", "X-Dev-Sales-Channel-Id",
    ];

    [Fact]
    public void Execution_scope_is_immutable_and_built_only_through_validated_factories()
    {
        var type = typeof(ExecutionScope);
        Assert.True(type.IsSealed, $"{Failure}: ExecutionScope must be sealed");
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        var settable = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } || p.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") == true)
            .Select(p => p.Name)
            .ToArray();
        Assert.True(settable.Length == 0, $"{Failure}: ExecutionScope properties must not be settable/init: {string.Join(", ", settable)}");
        Assert.Null(type.GetMethod("<Clone>$")); // no record 'with' cloning
    }

    [Fact]
    public void There_is_no_admin_or_maintenance_scope_principal()
    {
        var forbidden = Enum.GetNames<PrincipalType>()
            .Where(n => Regex.IsMatch(n, "admin|maintenance|super|root|bypass|unrestricted", RegexOptions.IgnoreCase))
            .ToArray();
        Assert.True(forbidden.Length == 0, $"{Failure}: forbidden principal types: {string.Join(", ", forbidden)}");
        Assert.Equal(["Anonymous", "Workforce", "Customer", "System"], Enum.GetNames<PrincipalType>());
    }

    [Fact]
    public void Claims_and_stage_one_identity_carry_no_business_scope()
    {
        var claimNames = typeof(CommercialClaimTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => $"{f.Name}={f.GetValue(null)}")
            .Where(c => Regex.IsMatch(c, "tenant|company|customer|account|channel|binding", RegexOptions.IgnoreCase))
            .ToArray();
        Assert.True(claimNames.Length == 0, $"{Failure}: business-scope claims are not trusted: {string.Join(", ", claimNames)}");

        var identityProps = typeof(AuthenticatedIdentityContext).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["CorrelationId", "Issuer", "PrincipalType", "Subject"], identityProps);
    }

    [Fact]
    public void Customer_scope_can_never_model_tenant_wide_access()
    {
        var customerFactory = typeof(ExecutionScope).GetMethod(nameof(ExecutionScope.CustomerBinding))!;
        Assert.DoesNotContain(customerFactory.GetParameters(), p => p.ParameterType == typeof(ScopeGrants));
        Assert.DoesNotContain(customerFactory.GetParameters(), p => p.ParameterType == typeof(PermissionSet)); // permissions only via the binding

        var grantFactories = typeof(ExecutionScope).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(ScopeGrants)))
            .Select(m => m.Name)
            .ToArray();
        Assert.Equal([nameof(ExecutionScope.WorkforceTenant)], grantFactories);
    }

    [Fact]
    public void No_trusted_tenant_or_customer_account_header_shortcuts()
    {
        var offending = Repository.TextFiles("src", ".cs", ".json")
            .Concat(Repository.TextFiles("frontend", ".ts", ".tsx"))
            .SelectMany(f => TrustedScopeShortcutHeaders
                .Where(h => File.ReadAllText(f).Contains(h, StringComparison.OrdinalIgnoreCase))
                .Select(h => $"{Repository.Relative(f)} -> {h}"))
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: scope must be resolved server-side, never from a trusted header: {string.Join("; ", offending)}");
    }

    [Fact]
    public void Selection_header_names_are_defined_only_in_the_header_contract()
    {
        var offending = Repository.TextFiles("src", ".cs")
            .Where(f => !f.Replace('\\', '/').EndsWith("Abstractions/Scope/ActiveContext.cs", StringComparison.Ordinal))
            .Where(f => SelectionHeaderLiteral().IsMatch(File.ReadAllText(f)))
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: use ContextHeaders constants instead of header literals: {string.Join(", ", offending)}");
    }

    [Fact]
    public void Customer_binding_concepts_stay_in_identity_security_or_scope_namespaces()
    {
        var offending = Repository.ProductionAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.Contains("CustomerBinding", StringComparison.Ordinal) || t.Name.Contains("BindingRecord", StringComparison.Ordinal))
            .Where(t => t.Namespace is null || !AllowedBindingNamespace().IsMatch(t.Namespace))
            .Select(t => t.FullName)
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: customer binding types outside Identity/Security/Scope: {string.Join(", ", offending)}");
    }

    [Fact]
    public void Production_assemblies_contain_no_fake_or_in_memory_identity_directory()
    {
        var offending = Repository.ProductionAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IIdentityDirectory).IsAssignableFrom(t) && !t.IsInterface)
            .Where(t => Regex.IsMatch(t.Name, "fake|inmemory|stub|dev|allow", RegexOptions.IgnoreCase))
            .Select(t => t.FullName)
            .ToArray();
        Assert.True(offending.Length == 0, $"{Failure}: production fake authorization source: {string.Join(", ", offending)}");
    }

    [GeneratedRegex("\"X-Active-(Company|Customer-Binding)-Id\"", RegexOptions.IgnoreCase)]
    private static partial Regex SelectionHeaderLiteral();

    [GeneratedRegex(@"(^|\.)(Identity|Security|Scope)(\.|$)")]
    private static partial Regex AllowedBindingNamespace();
}
