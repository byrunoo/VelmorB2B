using Commercial.Application.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;

namespace Commercial.Infrastructure.Persistence.Diagnostics;

/// <summary>Test sink for the Phase 0 diagnostic outbox handler. Keyed by outbox message id.</summary>
public sealed class DiagnosticPingReceipt
{
    public Guid MessageId { get; set; }

    public required string CorrelationId { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }
}

internal sealed class DiagnosticPingReceiptConfiguration : IEntityTypeConfiguration<DiagnosticPingReceipt>
{
    public void Configure(EntityTypeBuilder<DiagnosticPingReceipt> builder)
    {
        builder.ToTable("diagnostic_ping_receipts");
        builder.HasKey(r => r.MessageId);
        builder.Property(r => r.MessageId).ValueGeneratedNever();
        builder.Property(r => r.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(r => r.Note).HasMaxLength(DiagnosticPingMessage.MaxNoteLength);
    }
}

internal sealed class EfDiagnosticPingReceiptStore(CommercialDbContext db, TimeProvider clock) : IDiagnosticPingReceiptStore
{
    public async Task<bool> TryRecordAsync(Guid messageId, string correlationId, string? note, CancellationToken cancellationToken)
    {
        // INSERT .. ON CONFLICT DO NOTHING makes the effect idempotent even under concurrent duplicates.
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO commercial.diagnostic_ping_receipts (message_id, correlation_id, note, recorded_at_utc)
            VALUES (@message_id, @correlation_id, @note, @recorded_at)
            ON CONFLICT (message_id) DO NOTHING
            """,
            [
                new NpgsqlParameter("message_id", messageId),
                new NpgsqlParameter("correlation_id", correlationId),
                new NpgsqlParameter("note", (object?)note ?? DBNull.Value),
                new NpgsqlParameter("recorded_at", clock.GetUtcNow()),
            ],
            cancellationToken);

        return affected == 1;
    }
}

internal sealed class EfDiagnosticPingReadModel(CommercialDbContext db) : IDiagnosticPingReadModel
{
    public async Task<DiagnosticPingStatus?> GetAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var message = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => new { m.Id, m.Status, m.Attempts, m.ProcessedAtUtc, m.CorrelationId })
            .SingleOrDefaultAsync(cancellationToken);

        if (message is null)
        {
            return null;
        }

        var recorded = await db.DiagnosticPingReceipts.AsNoTracking().AnyAsync(r => r.MessageId == messageId, cancellationToken);
        return new DiagnosticPingStatus(message.Id, message.Status.ToString(), message.Attempts, message.ProcessedAtUtc, recorded, message.CorrelationId);
    }
}
