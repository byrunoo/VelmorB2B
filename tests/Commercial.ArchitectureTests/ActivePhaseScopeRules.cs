using System.Reflection;
using System.Text.RegularExpressions;
using Commercial.ArchitectureTests.Support;

namespace Commercial.ArchitectureTests;

/// <summary>
/// A018 — phase-aware scope guard. Reads docs/architecture/active-phase.json and applies the scope
/// data of THAT phase. Nothing here is "Phase 0 forever": the forbidden lists live in
/// PhaseScope/phase-scope.json and advance with each kickoff PR.
/// Also enforces the Phase 0 contract-test boundary (no invented external provider contracts).
/// </summary>
public sealed class ActivePhaseScopeRules
{
    private const string Failure = "ACTIVE_PHASE_SCOPE_VIOLATION";

    [Fact]
    public void A018_Active_phase_file_is_valid_and_has_scope_data()
    {
        var phase = ActivePhase.Load();
        Assert.True(phase.Phase >= 0, $"{Failure}: invalid phase number {phase.Phase}");
        Assert.False(string.IsNullOrWhiteSpace(phase.Name), $"{Failure}: active phase has no name");
        Assert.True(File.Exists(Repository.PathOf(phase.Mandate)), $"{Failure}: mandate '{phase.Mandate}' referenced by active-phase.json does not exist");
        Assert.True(
            PhaseScope.LoadAll().ContainsKey(phase.Phase),
            $"{Failure}: no A018 scope data for active phase {phase.Phase}. The kickoff PR must add it to {PhaseScope.RelativePath}.");
    }

    [Fact]
    public void A018_No_out_of_phase_projects()
    {
        var scope = CurrentScope();
        var violations = Repository.Projects
            .Where(p => p.IsProduction)
            .Select(p => p.Name)
            .Concat(Repository.ProductionAssemblies.Select(a => a.GetName().Name!))
            .Distinct(StringComparer.Ordinal)
            .Where(name => scope.ForbiddenProjectPatterns.Any(p => Regex.IsMatch(name, p)))
            .ToArray();

        Assert.True(violations.Length == 0, $"{Failure}: projects not allowed in active phase: {string.Join(", ", violations)}");
    }

    [Fact]
    public void A018_No_out_of_phase_business_types_or_modules()
    {
        var violations = Evaluate(CurrentScope(), Repository.ProductionAssemblies.SelectMany(SafeTypes)).ToArray();
        Assert.True(violations.Length == 0, $"{Failure}: {string.Join(", ", violations)}");
    }

    [Fact]
    public void A018_No_out_of_phase_frontend_screens()
    {
        var scope = CurrentScope();
        var violations = new[] { "frontend/admin/src", "frontend/b2b/src", "frontend/shared/src" }
            .SelectMany(d => Repository.TextFiles(d))
            .Select(Repository.Relative)
            .Where(path => path.Split('/').Any(seg =>
                scope.ForbiddenFrontendPathSegments.Contains(Path.GetFileNameWithoutExtension(seg).ToLowerInvariant(), StringComparer.Ordinal)))
            .ToArray();

        Assert.True(violations.Length == 0, $"{Failure}: frontend screens not allowed in active phase: {string.Join(", ", violations)}");
    }

    [Fact]
    public void A018_Guard_is_data_driven_not_phase0_forever()
    {
        // A later phase that approves Quote must not be blocked merely because Phase 0 banned it.
        var later = new PhaseScope { ForbiddenTypeNames = ["SalesOrder"], ForbiddenNamespaceSegments = [] };
        var phase0 = new PhaseScope { ForbiddenTypeNames = ["Quote", "SalesOrder"], ForbiddenNamespaceSegments = ["Quotes"] };
        Type[] candidate = [typeof(Fixtures.Quotes.Quote)];

        Assert.NotEmpty(Evaluate(phase0, candidate));
        Assert.Empty(Evaluate(later, candidate));
    }

    /// <summary>
    /// Approved Phase 1 identity/security concepts (mandate §7, identity-and-customer-membership.md) must not be blocked
    /// by the Phase 1 A018 data, while the commercial CustomerAccount entity stays forbidden. Recommended namespaces
    /// (Identity / Security / Tenancy) must not hit a forbidden module segment.
    /// </summary>
    [Theory]
    [InlineData("Principal")]
    [InlineData("PrincipalExternalIdentity")]
    [InlineData("CustomerPrincipalBinding")]
    [InlineData("CustomerBindingRoleAssignment")]
    [InlineData("CustomerUserInvitation")]
    [InlineData("Role")]
    [InlineData("Permission")]
    [InlineData("SecurityAuditEvent")]
    public void A018_Phase1_security_concepts_are_allowed_by_phase1_scope(string typeName)
    {
        var phase1 = PhaseScope.LoadAll()[1];
        Assert.DoesNotContain(typeName, phase1.ForbiddenTypeNames);
        Assert.Contains("CustomerAccount", phase1.ForbiddenTypeNames); // commercial entity remains Phase 2
        foreach (var ns in new[] { "Identity", "Security", "Tenancy", "Isolation", "Membership" })
        {
            Assert.DoesNotContain(ns, phase1.ForbiddenNamespaceSegments);
        }
    }

    [Fact]
    public void Phase0_contract_tests_must_not_invent_external_provider_contracts()
    {
        if (CurrentScope().ExternalContractTestsAllowed)
        {
            return;
        }

        var violations = Repository.TextFiles("tests/Commercial.ContractTests")
            .SelectMany(f => SourceText.Words(File.ReadAllText(f)).Select(w => (File: f, Match: ProviderTokens.Find(w))))
            .Where(x => x.Match is not null)
            .Select(x => $"{Repository.Relative(x.File)} -> {x.Match}")
            .Distinct()
            .ToArray();

        Assert.True(violations.Length == 0, $"PHASE0_EXTERNAL_CONTRACT_SCOPE_VIOLATION: {string.Join(", ", violations)}");
    }

    private static PhaseScope CurrentScope()
    {
        var phase = ActivePhase.Load();
        return PhaseScope.LoadAll().TryGetValue(phase.Phase, out var scope)
            ? scope
            : throw new InvalidOperationException($"{Failure}: no scope data for phase {phase.Phase}");
    }

    private static IEnumerable<string> Evaluate(PhaseScope scope, IEnumerable<Type> types) =>
        types
            .Where(t => !t.Name.Contains('<', StringComparison.Ordinal))
            .Where(t => t.Namespace?.StartsWith("Commercial.ArchitectureTests", StringComparison.Ordinal) != true || t.Namespace.Contains(".Fixtures", StringComparison.Ordinal))
            .SelectMany(t =>
            {
                var hits = new List<string>();
                var tick = t.Name.IndexOf('`', StringComparison.Ordinal);
                var simpleName = tick < 0 ? t.Name : t.Name[..tick];
                if (scope.ForbiddenTypeNames.Contains(simpleName, StringComparer.Ordinal))
                {
                    hits.Add($"type {t.FullName}");
                }

                var segment = (t.Namespace ?? string.Empty).Split('.').FirstOrDefault(s => scope.ForbiddenNamespaceSegments.Contains(s, StringComparer.Ordinal));
                if (segment is not null)
                {
                    hits.Add($"module namespace {t.Namespace} ({segment})");
                }

                return hits;
            })
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal);

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
