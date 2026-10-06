using System.Diagnostics;
using Commercial.Application.Abstractions.Messaging;
using Commercial.Application.Abstractions.Scope;
using Commercial.Domain.Identity;
using Commercial.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Commercial.Infrastructure.Messaging.Outbox;

public sealed record OutboxDispatchResult(int Claimed, int Processed, int Failed, int DeadLettered, int LeaseLost);

/// <summary>Entry point used by the Worker's Hangfire job. Safe to run concurrently on many workers.</summary>
public interface IOutboxDispatcher
{
    Task<OutboxDispatchResult> DispatchPendingAsync(CancellationToken cancellationToken);
}

internal sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IExecutionScopeAccessor scopeAccessor,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger) : IOutboxDispatcher
{
    private static readonly string ProcessInstanceId = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<OutboxDispatchResult> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        int claimed = 0, processed = 0, failed = 0, deadLettered = 0, leaseLost = 0;

        for (var batch = 0; batch < settings.MaxBatchesPerRun; batch++)
        {
            // Lease token is unique per claim, so two concurrent runs in the same process never share ownership.
            var lease = $"{settings.WorkerInstanceId ?? ProcessInstanceId}:{Guid.NewGuid():N}";

            IReadOnlyList<ClaimedOutboxMessage> messages;
            await using (var claimScope = scopeFactory.CreateAsyncScope())
            {
                var store = claimScope.ServiceProvider.GetRequiredService<IOutboxStore>();
                messages = await store.ClaimAsync(lease, settings.BatchSize, settings.LeaseDuration, cancellationToken);
            }

            if (messages.Count == 0)
            {
                break;
            }

            claimed += messages.Count;
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (await ProcessAsync(message, settings, cancellationToken))
                {
                    case Outcome.Processed: processed++; break;
                    case Outcome.Failed: failed++; break;
                    case Outcome.DeadLettered: deadLettered++; break;
                    case Outcome.LeaseLost: leaseLost++; break;
                }
            }
        }

        return new OutboxDispatchResult(claimed, processed, failed, deadLettered, leaseLost);
    }

    private async Task<Outcome> ProcessAsync(ClaimedOutboxMessage message, OutboxOptions settings, CancellationToken cancellationToken)
    {
        using var activity = CommercialTelemetry.ActivitySource.StartActivity("outbox.dispatch", ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "commercial.outbox");
        activity?.SetTag("messaging.message.id", message.Id);
        activity?.SetTag("commercial.outbox.type", message.Type);
        activity?.SetTag("commercial.outbox.attempt", message.Attempt);
        activity?.SetTag("commercial.correlation_id", message.CorrelationId);

        var executionScope = ExecutionScope.ForSystem(
            message.CorrelationId,
            causationId: message.Id.ToString("D"),
            tenantId: message.TenantId is { } t ? new TenantId(t) : null);

        using var ambient = scopeAccessor.Begin(executionScope);
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = message.CorrelationId,
            ["OutboxMessageId"] = message.Id,
            ["OutboxMessageType"] = message.Type,
        });

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        try
        {
            var handlers = scope.ServiceProvider.GetServices<IOutboxMessageHandler>()
                .Where(h => string.Equals(h.MessageType, message.Type, StringComparison.Ordinal))
                .ToList();

            if (handlers.Count == 0)
            {
                throw new InvalidOperationException($"No outbox handler registered for message type '{message.Type}'.");
            }

            var context = new OutboxMessageContext(
                message.Id,
                message.Type,
                message.Payload,
                message.CorrelationId,
                message.CausationId,
                message.TenantId,
                message.Attempt,
                message.OccurredAtUtc);

            foreach (var handler in handlers)
            {
                await handler.HandleAsync(context, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var next = clock.GetUtcNow() + settings.RetryDelayFor(message.Attempt);
            var status = await store.TryMarkFailedAsync(
                message.Id,
                message.Lease,
                $"{ex.GetType().Name}: {ex.Message}",
                next,
                settings.MaxAttempts,
                CancellationToken.None);

            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            if (status is null)
            {
                logger.LogWarning(ex, "Outbox message {MessageId} failed but lease was lost; another worker owns it", message.Id);
                CommercialTelemetry.OutboxLeaseLost.Add(1);
                return Outcome.LeaseLost;
            }

            if (status == OutboxStatus.DeadLettered)
            {
                logger.LogError(ex, "Outbox message {MessageId} dead-lettered after {Attempt} attempts", message.Id, message.Attempt);
                CommercialTelemetry.OutboxDeadLettered.Add(1);
                return Outcome.DeadLettered;
            }

            logger.LogWarning(ex, "Outbox message {MessageId} failed (attempt {Attempt}); retry at {NextAttempt}", message.Id, message.Attempt, next);
            CommercialTelemetry.OutboxFailed.Add(1);
            return Outcome.Failed;
        }

        if (await store.TryMarkProcessedAsync(message.Id, message.Lease, CancellationToken.None))
        {
            CommercialTelemetry.OutboxProcessed.Add(1);
            return Outcome.Processed;
        }

        // Handler ran but our lease was reclaimed meanwhile: the other owner will redeliver,
        // which is safe because handlers are idempotent.
        logger.LogWarning("Outbox message {MessageId} processed but lease was lost; completion skipped", message.Id);
        CommercialTelemetry.OutboxLeaseLost.Add(1);
        return Outcome.LeaseLost;
    }

    private enum Outcome
    {
        Processed,
        Failed,
        DeadLettered,
        LeaseLost,
    }
}
