namespace Commercial.Infrastructure.Messaging.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Rows claimed per claim transaction.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Upper bound of claim/process cycles per dispatcher run (one Hangfire job execution).</summary>
    public int MaxBatchesPerRun { get; set; } = 10;

    /// <summary>Lease length. Must comfortably exceed the time to process one batch.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>After this many attempts the row is dead-lettered instead of retried.</summary>
    public int MaxAttempts { get; set; } = 10;

    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Stable identity of this process; the per-claim lease token is derived from it.</summary>
    public string? WorkerInstanceId { get; set; }

    /// <summary>Exponential backoff: base * 2^(attempt-1), capped.</summary>
    public TimeSpan RetryDelayFor(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 20);
        var delay = TimeSpan.FromTicks(BaseRetryDelay.Ticks * (1L << exponent));
        return delay > MaxRetryDelay || delay < TimeSpan.Zero ? MaxRetryDelay : delay;
    }
}
