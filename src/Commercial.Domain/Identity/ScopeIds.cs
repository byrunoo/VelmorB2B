namespace Commercial.Domain.Identity;

// Strongly typed identifiers that the execution scope can carry.
// Phase 0 defines ONLY the identifiers; the Tenant/Company/CustomerAccount/SalesChannel
// models themselves are Phase 1+ deliverables. Cardinality (frozen): a Tenant owns many
// Companies, many SalesChannels (B2B portals) and many external-system connections.

public readonly record struct TenantId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct CompanyId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct CustomerAccountId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

/// <summary>A B2B portal / sales channel. Not 1:1 with any company or external connection.</summary>
public readonly record struct SalesChannelId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}
