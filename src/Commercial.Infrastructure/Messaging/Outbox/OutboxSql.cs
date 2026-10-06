namespace Commercial.Infrastructure.Messaging.Outbox;

/// <summary>
/// The outbox ownership protocol, kept in one place so architecture tests (A013) can assert it.
///
/// Claim (one short transaction): select claimable rows with FOR UPDATE SKIP LOCKED, stamp a
/// unique lease token + expiry, commit. Processing happens outside that transaction.
/// Completion/failure updates are guarded by the lease token, so a worker whose lease was
/// reclaimed by another worker can no longer change the row.
/// </summary>
public static class OutboxSql
{
    public const string Table = "commercial.outbox_messages";

    public const string Claim = """
        WITH candidates AS (
            SELECT id
            FROM commercial.outbox_messages
            WHERE processed_at_utc IS NULL
              AND status IN ('Pending', 'Processing')
              AND next_attempt_at_utc <= @now
              AND (locked_until_utc IS NULL OR locked_until_utc <= @now)
            ORDER BY next_attempt_at_utc, occurred_at_utc
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED
        )
        UPDATE commercial.outbox_messages AS m
        SET status = 'Processing',
            locked_by = @lease,
            locked_until_utc = @locked_until,
            attempts = m.attempts + 1
        FROM candidates
        WHERE m.id = candidates.id
        RETURNING m.id, m.type, m.payload::text, m.correlation_id, m.causation_id, m.tenant_id, m.attempts, m.occurred_at_utc;
        """;

    public const string MarkProcessed = """
        UPDATE commercial.outbox_messages
        SET status = 'Processed',
            processed_at_utc = @now,
            last_error = NULL,
            locked_by = NULL,
            locked_until_utc = NULL
        WHERE id = @id
          AND locked_by = @lease
          AND status = 'Processing';
        """;

    public const string MarkFailed = """
        UPDATE commercial.outbox_messages
        SET status = CASE WHEN attempts >= @max_attempts THEN 'DeadLettered' ELSE 'Pending' END,
            last_error = @error,
            next_attempt_at_utc = @next_attempt,
            locked_by = NULL,
            locked_until_utc = NULL
        WHERE id = @id
          AND locked_by = @lease
          AND status = 'Processing'
        RETURNING status;
        """;
}
