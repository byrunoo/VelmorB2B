using Commercial.Application.Abstractions.Messaging;
using Commercial.Application.Abstractions.Persistence;
using Commercial.Application.Abstractions.Requests;
using Commercial.Application.Abstractions.Scope;
using Commercial.Application.Diagnostics;
using Commercial.Domain.Identity;
using Commercial.Infrastructure.Messaging.Outbox;
using Commercial.Infrastructure.Persistence;
using Commercial.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Commercial.IntegrationTests.Outbox;

public sealed class OutboxTransactionAndIdempotencyTests(ContainersFixture containers)
{
    [Fact]
    public async Task Outbox_row_commits_with_the_application_transaction_and_carries_scope()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var provider = containers.BuildServices(cs);

        DiagnosticPingAccepted accepted;
        using (provider.GetRequiredService<IExecutionScopeAccessor>().Begin(ExecutionScope.SystemInfrastructure("corr-commit-1", "cause-1")))
        {
            await using var scope = provider.CreateAsyncScope();
            accepted = await scope.ServiceProvider.GetRequiredService<IRequestDispatcher>()
                .SendAsync(new EnqueueDiagnosticPing("commit"), CancellationToken.None);
        }

        var row = await OutboxSeed.GetAsync(provider, accepted.MessageId);
        Assert.Equal(DiagnosticPingMessage.Type, row.Type);
        Assert.Equal("corr-commit-1", row.CorrelationId);
        Assert.Equal("cause-1", row.CausationId);
        Assert.Null(row.TenantId); // no business scope -> no scope metadata
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(TimeSpan.Zero, row.OccurredAtUtc.Offset);
    }

    [Fact]
    public async Task Outbox_row_is_rolled_back_with_a_failed_transaction()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var provider = containers.BuildServices(cs);

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteInTransactionAsync<Guid>(
                _ =>
                {
                    outbox.Enqueue("test.rollback.v1", new TestPayload(1));
                    throw new InvalidOperationException("business step failed after enqueue");
                },
                CancellationToken.None));
        }

        Assert.Empty(await OutboxSeed.AllAsync(provider));
    }

    [Fact]
    [Trait("A013", "duplicate-delivery-idempotency")]
    public async Task Duplicate_and_concurrent_delivery_produce_a_single_side_effect()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var provider = containers.BuildServices(cs);

        Guid messageId;
        await using (var scope = provider.CreateAsyncScope())
        {
            messageId = (await scope.ServiceProvider.GetRequiredService<IRequestDispatcher>()
                .SendAsync(new EnqueueDiagnosticPing("dup"), CancellationToken.None)).MessageId;
        }

        var row = await OutboxSeed.GetAsync(provider, messageId);
        var context = new OutboxMessageContext(row.Id, row.Type, row.Payload, row.CorrelationId, row.CausationId, row.TenantId, 1, row.OccurredAtUtc);

        // Sequential redelivery + concurrent duplicates, each through a fresh scope like a real dispatcher.
        for (var i = 0; i < 3; i++)
        {
            await DeliverAsync(provider, context);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => DeliverAsync(provider, context))));

        Assert.Equal(1, await CountReceiptsAsync(provider, messageId));
    }

    [Fact]
    [Trait("A013", "duplicate-delivery-idempotency")]
    public async Task Redelivery_after_effect_without_ack_does_not_duplicate_the_effect()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var provider = containers.BuildServices(cs, before: s => s.AddSingleton<TimeProvider>(clock));

        Guid messageId;
        await using (var scope = provider.CreateAsyncScope())
        {
            messageId = (await scope.ServiceProvider.GetRequiredService<IRequestDispatcher>()
                .SendAsync(new EnqueueDiagnosticPing("crash-after-effect"), CancellationToken.None)).MessageId;
        }

        // Worker 1 claims, applies the effect, then crashes before marking the row processed.
        await using (var crashed = provider.CreateAsyncScope())
        {
            var claimed = Assert.Single(await crashed.ServiceProvider.GetRequiredService<IOutboxStore>()
                .ClaimAsync("crashing-worker", 10, TimeSpan.FromSeconds(30), CancellationToken.None));
            await DeliverAsync(provider, new OutboxMessageContext(claimed.Id, claimed.Type, claimed.Payload, claimed.CorrelationId, claimed.CausationId, claimed.TenantId, claimed.Attempt, claimed.OccurredAtUtc));
        }

        Assert.Equal(1, await CountReceiptsAsync(provider, messageId));

        // Lease expires; the dispatcher redelivers the same message.
        clock.Advance(TimeSpan.FromMinutes(5));
        var result = await provider.GetRequiredService<IOutboxDispatcher>().DispatchPendingAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, await CountReceiptsAsync(provider, messageId)); // still exactly one effect
        Assert.Equal(OutboxStatus.Processed, (await OutboxSeed.GetAsync(provider, messageId)).Status);
    }

    private static async Task DeliverAsync(IServiceProvider provider, OutboxMessageContext context)
    {
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IOutboxMessageHandler>().Single(h => h.MessageType == DiagnosticPingMessage.Type);
        await handler.HandleAsync(context, CancellationToken.None);
    }

    private static async Task<int> CountReceiptsAsync(IServiceProvider provider, Guid messageId)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CommercialDbContext>().DiagnosticPingReceipts.CountAsync(r => r.MessageId == messageId);
    }
}
