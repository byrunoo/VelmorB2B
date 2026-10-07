namespace Commercial.Domain.Identity;

// Strongly typed identifiers carried by the execution scope. Only identifiers — the Tenant / Company / Principal /
// membership models are separate Phase 1 workstreams. Cardinality (frozen): a Tenant owns many Companies; one global
// Principal may hold many memberships (company grants, customer bindings) across companies AND tenants; a normal
// request executes under exactly one active business scope.

public readonly record struct TenantId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct CompanyId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

/// <summary>Opaque customer-account security reference (no CustomerAccount commercial entity in Phase 1).</summary>
public readonly record struct CustomerAccountId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

/// <summary>Global identity (one per human), never tenant-owned.</summary>
public readonly record struct PrincipalId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

/// <summary>One customer membership (CustomerPrincipalBinding) — the opaque active-context handle for CUSTOMER requests.</summary>
public readonly record struct CustomerBindingId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}
