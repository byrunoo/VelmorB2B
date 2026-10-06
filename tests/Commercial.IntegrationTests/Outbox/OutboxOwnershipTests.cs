using Commercial.Application.Abstractions.Messaging;
using Commercial.Infrastructure.Messaging.Outbox;
using Commercial.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Commercial.IntegrationTests.Outbox;

/// <summary>A013 — multi-worker ownership, lease expiry recovery, retry/dead-letter. Real PostgreSQL.</summary>
public sealed class OutboxOwnershipTests(ContainersFixture containers)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("A013", "multi-worker-claim")]
    public async Task Concurrent_claims_never_hand_the_same_row_to_two_workers()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var seeder = containers.BuildServices(cs);
        var ids = await OutboxSeed.EnqueueAsync(seeder, RecordingHandler.Type, 300);

        const int workers = 6;
        var providers = Enumerable.Range(0, workers).Select(_ => containers.BuildServices(cs)).ToList();
        using var start = new Barrier(workers);

        var claims = await Task.WhenAll(providers.Select((p, w) => Task.Run(async () =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            var mine = new List<Guid>();
            while (true)
            {
                await using var scope = p.CreateAsyncScope();
                var batch = await scope.ServiceProvider.GetRequiredService<IOutboxStore>()
                    .ClaimAsync($"worker-{w}:{Guid.NewGuid():N}", 7, Lease, CancellationToken.None);
                if (batch.Count == 0)
                {
                    return mine;
                }

                mine.AddRange(batch.Select(m => m.Id));
            }
        })));

        foreach (var p in providers)
        {
            await p.DisposeAsync();
        }

        var all = claims.SelectMany(c => c).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());          // no row claimed twice
        Assert.Equal(ids.OrderBy(x => x), all.OrderBy(x => x));  // every row claimed once
        Assert.True(claims.Count(c => c.Count > 0) > 1, "expected the work to be spread across several workers");
    }

    [Fact]
    [Trait("A013", "multi-worker-claim")]
    public async Task Concurrent_dispatchers_process_each_message_once_with_one_active_owner()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        var sink = new RecordingSink();
        var providers = Enumerable.Range(0, 4)
            .Select(w => containers.BuildServices(
                cs,
                after: s => s.AddScoped<IOutboxMessageHandler>(_ => new RecordingHandler(sink, $"worker-{w}")),
                settings: new Dictionary<string, string?>
                {
                    ["Outbox:BatchSize"] = "5",
                    ["Outbox:MaxBatchesPerRun"] = "1000",
                    ["Outbox:WorkerInstanceId"] = $"worker-{w}",
                }))
            .ToList();

        var ids = await OutboxSeed.EnqueueAsync(providers[0], RecordingHandler.Type, 200);

        var results = await Task.WhenAll(providers.Select(p => Task.Run(() =>
            p.GetRequiredService<IOutboxDispatcher>().DispatchPendingAsync(CancellationToken.None))));

        Assert.Empty(sink.Overlaps);
        Assert.Equal(200, results.Sum(r => r.Processed));
        Assert.All(ids, id => Assert.Equal(1, sink.Deliveries[id]));
        Assert.True(sink.ProcessedBy.Select(p => p.Worker).Distinct().Count() > 1, "expected several workers to participate");

        var rows = await OutboxSeed.AllAsync(providers[0]);
        Assert.All(rows, r =>
        {
            Assert.Equal(OutboxStatus.Processed, r.Status);
            Assert.Null(r.LockedBy);
            Assert.Null(r.LockedUntilUtc);
            Assert.Equal(1, r.Attempts);
        });

        foreach (var p in providers)
        {
            await p.DisposeAsync();
        }
    }

    [Fact]
    [Trait("A013", "lease-expiry-recovery")]
    public async Task Expired_lease_is_reclaimed_and_the_stale_owner_can_no_longer_complete()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var provider = containers.BuildServices(cs, before: s => s.AddSingleton<TimeProvider>(clock));
        var id = (await OutboxSeed.EnqueueAsync(provider, RecordingHandler.Type, 1)).Single();

        await using var scopeA = provider.CreateAsyncScope();
        await using var scopeB = provider.CreateAsyncScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IOutboxStore>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<IOutboxStore>();

        var claimedByA = await storeA.ClaimAsync("A", 10, Lease, CancellationToken.None);
        Assert.Single(claimedByA);
        Assert.Empty(await storeB.ClaimAsync("B", 10, Lease, CancellationToken.None)); // live lease is respected

        clock.Advance(Lease + TimeSpan.FromSeconds(1)); // worker A "hangs" past its lease

        var claimedByB = await storeB.ClaimAsync("B", 10, Lease, CancellationToken.None);
        Assert.Equal(id, Assert.Single(claimedByB).Id);
        Assert.Equal(2, claimedByB[0].Attempt);

        Assert.False(await storeA.TryMarkProcessedAsync(id, "A", CancellationToken.None));                // stale owner rejected
        Assert.Null(await storeA.TryMarkFailedAsync(id, "A", "late failure", clock.GetUtcNow(), 10, CancellationToken.None));
        Assert.True(await storeB.TryMarkProcessedAsync(id, "B", CancellationToken.None));                 // current owner wins

        var row = await OutboxSeed.GetAsync(provider, id);
        Assert.Equal(OutboxStatus.Processed, row.Status);
        Assert.Null(row.LockedBy);
        Assert.Null(row.LastError);
    }

    [Fact]
    [Trait("A013", "lease-expiry-recovery")]
    public async Task Message_of_a_crashed_worker_is_redelivered_after_lease_expiry()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var sink = new RecordingSink();
        await using var provider = containers.BuildServices(
            cs,
            before: s => s.AddSingleton<TimeProvider>(clock),
            after: s => s.AddScoped<IOutboxMessageHandler>(_ => new RecordingHandler(sink, "survivor")));
        var id = (await OutboxSeed.EnqueueAsync(provider, RecordingHandler.Type, 1)).Single();

        await using (var crashed = provider.CreateAsyncScope())
        {
            // Claim committed, then the process "dies": no completion, no failure update.
            Assert.Single(await crashed.ServiceProvider.GetRequiredService<IOutboxStore>().ClaimAsync("crashed-worker", 10, Lease, CancellationToken.None));
        }

        var dispatcher = provider.GetRequiredService<IOutboxDispatcher>();
        Assert.Equal(0, (await dispatcher.DispatchPendingAsync(CancellationToken.None)).Claimed); // still leased

        clock.Advance(TimeSpan.FromMinutes(3)); // > default 2 min lease
        var result = await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, sink.Deliveries[id]);
        var row = await OutboxSeed.GetAsync(provider, id);
        Assert.Equal(OutboxStatus.Processed, row.Status);
        Assert.Equal(2, row.Attempts);
    }

    [Fact]
    public async Task Failing_handler_is_retried_with_backoff_then_dead_lettered()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var provider = containers.BuildServices(
            cs,
            before: s => s.AddSingleton<TimeProvider>(clock),
            after: s => s.AddScoped<IOutboxMessageHandler, FailingHandler>(),
            settings: new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "2", ["Outbox:BaseRetryDelay"] = "00:00:10" });
        var id = (await OutboxSeed.EnqueueAsync(provider, FailingHandler.Type, 1)).Single();
        var dispatcher = provider.GetRequiredService<IOutboxDispatcher>();

        var first = await dispatcher.DispatchPendingAsync(CancellationToken.None);
        Assert.Equal(1, first.Failed);
        var afterFirst = await OutboxSeed.GetAsync(provider, id);
        Assert.Equal(OutboxStatus.Pending, afterFirst.Status);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(10), afterFirst.NextAttemptAtUtc);
        Assert.Contains("Simulated handler failure", afterFirst.LastError, StringComparison.Ordinal);
        Assert.Null(afterFirst.LockedBy);

        Assert.Equal(0, (await dispatcher.DispatchPendingAsync(CancellationToken.None)).Claimed); // backoff respected

        clock.Advance(TimeSpan.FromSeconds(11));
        var second = await dispatcher.DispatchPendingAsync(CancellationToken.None);
        Assert.Equal(1, second.DeadLettered);
        Assert.Equal(OutboxStatus.DeadLettered, (await OutboxSeed.GetAsync(provider, id)).Status);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, (await dispatcher.DispatchPendingAsync(CancellationToken.None)).Claimed); // dead letters are not reclaimed
    }

    [Fact]
    public async Task Message_without_a_handler_is_not_silently_dropped()
    {
        var cs = await containers.CreateMigratedDatabaseAsync();
        await using var provider = containers.BuildServices(cs);
        var id = (await OutboxSeed.EnqueueAsync(provider, "test.unknown.v1", 1)).Single();

        var result = await provider.GetRequiredService<IOutboxDispatcher>().DispatchPendingAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        var row = await OutboxSeed.GetAsync(provider, id);
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Contains("No outbox handler", row.LastError, StringComparison.Ordinal);
    }
}
