using Commercial.Application.Abstractions.Scope;
using Commercial.Domain.Identity;

namespace Commercial.Infrastructure.Messaging.Outbox;

/// <summary>
/// Job/outbox scope metadata contract (Phase 1 WS2). Background work NEVER reuses the producer's ambient scope and
/// NEVER falls back to an unrestricted SYSTEM scope: the consumer builds a FRESH scope from persisted metadata only.
///
/// <list type="bullet">
/// <item>no metadata → <see cref="ExecutionScopeKind.SystemInfrastructure"/> (no business access);</item>
/// <item>tenant + company (+ optional account / binding) → <see cref="ExecutionScopeKind.SystemBusiness"/>;</item>
/// <item>anything partial (e.g. tenant without company) → <see cref="InvalidExecutionScopeException"/> (fail closed).</item>
/// </list>
///
/// The outbox table currently persists only <c>tenant_id</c> (no company / account / binding columns; no migration
/// in WS2). Therefore business-scoped enqueue is refused by <see cref="FromProducerScope"/> until the columns are
/// added together with the first business-scoped producer, and rows carrying a tenant id dispatch fail-closed.
/// </summary>
public sealed record OutboxScopeMetadata(
    TenantId? TenantId,
    CompanyId? CompanyId,
    CustomerAccountId? CustomerAccountId,
    CustomerBindingId? CustomerBindingId)
{
    public static OutboxScopeMetadata None { get; } = new(null, null, null, null);

    public bool IsEmpty => TenantId is null && CompanyId is null && CustomerAccountId is null && CustomerBindingId is null;

    /// <summary>What the CURRENT persistence can store faithfully for the producing scope.</summary>
    public static OutboxScopeMetadata FromProducerScope(ExecutionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.HasBusinessScope)
        {
            return None;
        }

        throw new InvalidExecutionScopeException(
        [
            "business-scoped outbox messages need company/customer scope metadata columns, which are not persisted yet " +
            "(added with the first business-scoped producer); refusing to persist partial scope",
        ]);
    }

    /// <summary>Fresh consumer scope from persisted metadata (fail closed on partial metadata).</summary>
    public ExecutionScope ToExecutionScope(string correlationId, string causationId)
    {
        if (IsEmpty)
        {
            return ExecutionScope.SystemInfrastructure(correlationId, causationId);
        }

        if (TenantId is not { } tenant || CompanyId is not { } company)
        {
            throw new InvalidExecutionScopeException(["outbox scope metadata is partial (tenant and company are both required)"]);
        }

        return ExecutionScope.SystemBusiness(tenant, company, CustomerAccountId, CustomerBindingId, correlationId, causationId);
    }
}
