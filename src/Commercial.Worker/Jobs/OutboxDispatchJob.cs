using Commercial.Infrastructure.Messaging.Outbox;
using Hangfire;

namespace Commercial.Worker.Jobs;

/// <summary>
/// Hangfire-triggered outbox dispatch. Orchestration only: ownership/lease safety lives in the
/// dispatcher's database claim, NOT in Hangfire scheduling — many workers may run this concurrently.
/// </summary>
public sealed class OutboxDispatchJob(IOutboxDispatcher dispatcher, ILogger<OutboxDispatchJob> logger)
{
    public const string RecurringJobId = "commercial:outbox-dispatch";

    /// <summary>Every 5 seconds (Hangfire 6-field cron with seconds).</summary>
    public const string Schedule = "*/5 * * * * *";

    [AutomaticRetry(Attempts = 0)] // retries are per message (outbox backoff), not per job run
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchPendingAsync(cancellationToken);
        if (result.Claimed > 0)
        {
            logger.LogInformation(
                "Outbox dispatch: claimed={Claimed} processed={Processed} failed={Failed} deadLettered={DeadLettered} leaseLost={LeaseLost}",
                result.Claimed, result.Processed, result.Failed, result.DeadLettered, result.LeaseLost);
        }
    }
}
