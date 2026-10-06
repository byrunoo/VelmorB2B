using System.Data;
using System.Data.Common;
using Commercial.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Commercial.Infrastructure.Messaging.Outbox;

public sealed record ClaimedOutboxMessage(
    Guid Id,
    string Type,
    string Payload,
    string CorrelationId,
    string? CausationId,
    Guid? TenantId,
    int Attempt,
    DateTimeOffset OccurredAtUtc,
    string Lease);

/// <summary>Lease-based claim/complete/fail operations (see <see cref="OutboxSql"/>).</summary>
public interface IOutboxStore
{
    Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(string lease, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <returns><c>false</c> when the lease is no longer owned (row was reclaimed by another worker).</returns>
    Task<bool> TryMarkProcessedAsync(Guid id, string lease, CancellationToken cancellationToken);

    /// <returns>The new status, or <c>null</c> when the lease is no longer owned.</returns>
    Task<OutboxStatus?> TryMarkFailedAsync(Guid id, string lease, string failureReason, DateTimeOffset nextAttemptAtUtc, int maxAttempts, CancellationToken cancellationToken);
}

internal sealed class OutboxStore(CommercialDbContext db, TimeProvider clock) : IOutboxStore
{
    private const int MaxErrorLength = 4000;

    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(
        string lease,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lease);
        var now = clock.GetUtcNow();
        var connection = await OpenAsync(cancellationToken);

        // Short claim transaction: lock candidates (skipping rows locked by other claimers),
        // stamp the lease, commit. Processing happens after commit, outside this transaction.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = OutboxSql.Claim;
        command.Parameters.Add(new NpgsqlParameter("now", now));
        command.Parameters.Add(new NpgsqlParameter("batch_size", batchSize));
        command.Parameters.Add(new NpgsqlParameter("lease", lease));
        command.Parameters.Add(new NpgsqlParameter("locked_until", now + leaseDuration));

        var claimed = new List<ClaimedOutboxMessage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new ClaimedOutboxMessage(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    reader.GetInt32(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    lease));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return claimed.OrderBy(m => m.OccurredAtUtc).ToList();
    }

    public async Task<bool> TryMarkProcessedAsync(Guid id, string lease, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = OutboxSql.MarkProcessed;
        command.Parameters.Add(new NpgsqlParameter("now", clock.GetUtcNow()));
        command.Parameters.Add(new NpgsqlParameter("id", id));
        command.Parameters.Add(new NpgsqlParameter("lease", lease));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<OutboxStatus?> TryMarkFailedAsync(
        Guid id,
        string lease,
        string failureReason,
        DateTimeOffset nextAttemptAtUtc,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = OutboxSql.MarkFailed;
        command.Parameters.Add(new NpgsqlParameter("id", id));
        command.Parameters.Add(new NpgsqlParameter("lease", lease));
        command.Parameters.Add(new NpgsqlParameter("error", failureReason.Length > MaxErrorLength ? failureReason[..MaxErrorLength] : failureReason));
        command.Parameters.Add(new NpgsqlParameter("next_attempt", nextAttemptAtUtc));
        command.Parameters.Add(new NpgsqlParameter("max_attempts", maxAttempts));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string status ? Enum.Parse<OutboxStatus>(status) : null;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        // Opening through EF keeps the DB-session initializer hook in the path.
        await db.Database.OpenConnectionAsync(cancellationToken);
        return db.Database.GetDbConnection();
    }
}
