using System.Text.Json;
using Commercial.Application.Abstractions.Messaging;
using Commercial.Application.Abstractions.Scope;
using Commercial.Infrastructure.Persistence;

namespace Commercial.Infrastructure.Messaging.Outbox;

/// <summary>
/// Adds the outbox row to the current <see cref="CommercialDbContext"/>; it is committed by the
/// unit of work together with the rest of the transaction.
/// </summary>
internal sealed class EfOutbox(CommercialDbContext db, IExecutionScopeAccessor scope, TimeProvider clock) : IOutbox
{
    public Guid Enqueue<TPayload>(string messageType, TPayload payload)
        where TPayload : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        var current = scope.Current;
        var metadata = OutboxScopeMetadata.FromProducerScope(current); // fail closed: never persists partial scope
        var now = clock.GetUtcNow();

        var message = new OutboxMessage
        {
            Id = Guid.CreateVersion7(now),
            OccurredAtUtc = now,
            Type = messageType,
            Payload = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
            CorrelationId = current.CorrelationId,
            CausationId = current.CausationId,
            TenantId = metadata.TenantId?.Value,
            Attempts = 0,
            NextAttemptAtUtc = now,
            Status = OutboxStatus.Pending,
        };

        db.OutboxMessages.Add(message);
        return message.Id;
    }
}
